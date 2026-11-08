using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Trailwright.Model;

namespace Trailwright.App;

/// <summary>Local HTTP server for the app's own window: the web UI (embedded files) and a JSON API.</summary>
public static class UiServer
{
    public sealed record PlaceReq(string Guid, int[] Pos, int[]? Rot, int? Color, int? Color2, bool AllowFloating);
    public sealed record IdsReq(int[] Ids, int? Color, int? Color2);
    public sealed record TransformReq(int Id, int[]? Pos, int[]? Rot, bool AllowFloating);
    public sealed record SliderReq(int Id, int Index, float Value);
    public sealed record KeysReq(int Id, string? Positive, string? Negative);
    public sealed record InfoReq(string? Name, string? Description);
    public sealed record OpenReq(string Path, bool Discard);
    public sealed record NewReq(string? Name, bool Discard);
    public sealed record SaveReq(bool AsNew);
    public sealed record SettingsReq(bool? Unrestricted, bool? AiEnabled, int? AiPort, bool? AiCanSave);
    public sealed record PictureReq(string DataUrl, int Revision);

    static readonly System.Text.RegularExpressions.Regex ModelFile = new(@"^[0-9a-f\-]{36}_\d{1,2}\.bin$");

    public static async Task<WebApplication> Start(AppState s, int port)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            o.SerializerOptions.NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals;
        });
        var app = builder.Build();

        // Only our own window may call the API. The Host check stops DNS rebinding (a web page whose
        // domain resolves to 127.0.0.1); the Origin check stops other pages posting to us.
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Host.Host is not ("127.0.0.1" or "localhost")) { ctx.Response.StatusCode = 421; return; }
            if (ctx.Request.Path.StartsWithSegments("/api") && ctx.Request.Method != "GET")
            {
                var origin = ctx.Request.Headers.Origin.ToString();
                if (origin.Length > 0 && origin != $"http://127.0.0.1:{port}") { ctx.Response.StatusCode = 403; return; }
            }
            // never let the window keep an old copy of the page or the scripts after an update
            ctx.Response.Headers.CacheControl = "no-store";
            try { await next(); }
            catch (EditException ex) { await Fail(ctx, ex.Message); }
            catch (Exception ex) when (ex is InvalidDataException or UnauthorizedAccessException or IOException or InvalidOperationException
                                          or NotSupportedException or ArgumentException or OverflowException)
            { await Fail(ctx, ex.Message); }
        });

        app.MapGet("/api/state", () => Results.Json(s.StateView(), AppState.Json));
        app.MapGet("/api/catalog", () => Results.Json(new
        {
            version = typeof(UiServer).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion,
            palette = s.Catalog.Palette,
            categories = s.Catalog.CategoryNames,
            actions = s.Catalog.Actions,
            blocks = s.Catalog.Blocks.Where(b => b.Template != null).Select(b => new
            {
                guid = b.Guid, name = b.Name, category = s.Catalog.CategoryName(b), sort = b.Sort,
                gridMin = b.GridMin, gridMax = b.GridMax, paintable = b.Tintable, color2 = b.SecondaryColor,
                knobs = b.Knobs.Count > 0 ? b.Knobs[0] : [],
                sliders = b.Sliders,
            }),
        }, AppState.Json));
        app.MapGet("/api/scene", () => Results.Json(s.SceneView(), AppState.Json));
        app.MapGet("/api/block/{id:int}", (int id) => { lock (s.Editor.Gate) return Results.Json(s.BlockView(s.Editor.Get(id), true), AppState.Json); });
        app.MapGet("/api/library", () => Results.Json(s.Library.List(), AppState.Json));

        // block models from the local cache (extracted from the user's own game install)
        app.MapGet("/api/models", () =>
        {
            var idx = Path.Combine(s.ModelsDir, "index.json");
            return File.Exists(idx) ? Results.File(File.ReadAllBytes(idx), "application/json") : Results.Json(new { blocks = new { } });
        });
        app.MapGet("/api/models/{file}", (string file) =>
        {
            if (!ModelFile.IsMatch(file)) return Results.NotFound();
            var p = Path.Combine(s.ModelsDir, file);
            return File.Exists(p) ? Results.File(File.ReadAllBytes(p), "application/octet-stream") : Results.NotFound();
        });
        app.MapPost("/api/diagnostics", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync();
            if (body.Length > 64_000) return Results.BadRequest();
            var dir = Path.GetDirectoryName(s.ModelsDir)!;
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "diagnostics.json"), body);
            return Results.Ok();
        });
        app.MapPost("/api/picture", (PictureReq r) =>
        {
            const string prefix = "data:image/png;base64,";
            if (!r.DataUrl.StartsWith(prefix)) throw new EditException("expected a PNG data URL");
            var img = Trailwright.Format.Png.Decode(Convert.FromBase64String(r.DataUrl[prefix.Length..]));
            if (img.Width != 512 || img.Height != 512) throw new EditException("picture must be 512x512");
            s.LivePicture = (img, r.Revision);
            return Results.Ok();
        });
        app.MapGet("/api/library/picture", (string path) => Results.File(File.ReadAllBytes(s.Library.Check(path)), "image/png"));

        app.MapPost("/api/open", (OpenReq r) =>
        {
            var b = s.Library.Open(r.Path);
            lock (s.Editor.Gate)
            {
                if (s.Editor.Dirty && !r.Discard) return Results.Json(new { needsConfirm = true });
                s.Editor.Replace(b, s.Library.Check(r.Path));
            }
            return Results.Json(s.StateView(), AppState.Json);
        });
        app.MapPost("/api/new", (NewReq r) =>
        {
            lock (s.Editor.Gate)
            {
                if (s.Editor.Dirty && !r.Discard) return Results.Json(new { needsConfirm = true });
                s.Editor.Replace(s.Library.New(r.Name ?? "New build"), null);
            }
            return Results.Json(s.StateView(), AppState.Json);
        });
        app.MapPost("/api/place", (PlaceReq r) =>
        {
            var t = s.Catalog.Resolve(r.Guid) ?? throw new EditException("unknown block type");
            var rot = r.Rot is { Length: 3 } ? AppState.ParseRot(r.Rot[0], r.Rot[1], r.Rot[2]) : Rot.Identity;
            var res = s.Editor.Place(t, new Int3(r.Pos[0], r.Pos[1], r.Pos[2]), rot, r.Color, r.Color2, r.AllowFloating);
            return Results.Json(new { id = res.Block.Id, connections = res.Connections, warnings = res.Warnings }, AppState.Json);
        });
        app.MapPost("/api/remove", (IdsReq r) => { s.Editor.Remove(r.Ids); return Results.Ok(); });
        app.MapPost("/api/transform", (TransformReq r) =>
        {
            var res = s.Editor.Transform(r.Id,
                r.Pos is { Length: 3 } ? new Int3(r.Pos[0], r.Pos[1], r.Pos[2]) : null,
                r.Rot is { Length: 3 } ? AppState.ParseRot(r.Rot[0], r.Rot[1], r.Rot[2]) : null, r.AllowFloating);
            return Results.Json(new { connections = res.Connections, warnings = res.Warnings }, AppState.Json);
        });
        app.MapPost("/api/color", (IdsReq r) => { s.Editor.SetColor(r.Ids, r.Color, r.Color2); return Results.Ok(); });
        app.MapPost("/api/slider", (SliderReq r) =>
        {
            var (v, capped) = s.Editor.SetSlider(r.Id, r.Index, r.Value);
            return Results.Json(new { value = v, capped }, AppState.Json);
        });
        app.MapPost("/api/keys", (KeysReq r) => { s.Editor.SetKeys(r.Id, s.ParseAction(r.Positive), s.ParseAction(r.Negative)); return Results.Ok(); });
        app.MapPost("/api/info", (InfoReq r) => { s.Editor.SetInfo(r.Name, r.Description); return Results.Ok(); });
        app.MapPost("/api/undo", () => Results.Json(new { ok = s.Editor.Undo() }));
        app.MapPost("/api/redo", () => Results.Json(new { ok = s.Editor.Redo() }));
        app.MapPost("/api/save", (SaveReq r) => Results.Json(new { path = s.Save(r.AsNew) }));
        app.MapPost("/api/settings", async (SettingsReq r) =>
        {
            if (r.Unrestricted != null) s.Editor.Unrestricted = r.Unrestricted.Value;
            if (r.AiCanSave != null) s.Settings.AiCanSave = r.AiCanSave.Value;
            bool restart = false;
            if (r.AiPort is int p && p != s.Settings.AiPort)
            {
                if (p < 1024 || p > 65535) throw new EditException("port must be 1024..65535");
                s.Settings.AiPort = p;
                restart = true;
            }
            if (r.AiEnabled != null && r.AiEnabled.Value != s.Settings.AiEnabled) { s.Settings.AiEnabled = r.AiEnabled.Value; restart = true; }
            s.Settings.Save();
            if (restart) await s.Mcp.SetEnabled(s.Settings.AiEnabled);
            return Results.Json(s.StateView(), AppState.Json);
        });

        // embedded web UI
        var asm = Assembly.GetExecutingAssembly();
        // MSBuild names resources in subfolders with a backslash (www/vendor\x.js); map them to URL paths
        var files = asm.GetManifestResourceNames().Where(n => n.StartsWith("www/"))
                       .ToDictionary(n => n[4..].Replace('\\', '/'), n => n, StringComparer.OrdinalIgnoreCase);
        app.MapGet("/{**path}", (string? path) =>
        {
            path = string.IsNullOrEmpty(path) ? "index.html" : path;
            var stream = files.TryGetValue(path, out var res) ? asm.GetManifestResourceStream(res) : null;
            if (stream == null) return Results.NotFound();
            var type = Path.GetExtension(path) switch
            {
                ".html" => "text/html; charset=utf-8", ".js" => "text/javascript; charset=utf-8", ".css" => "text/css; charset=utf-8",
                ".svg" => "image/svg+xml", ".png" => "image/png", ".ico" => "image/x-icon", _ => "application/octet-stream",
            };
            return Results.Stream(stream, type);
        });

        await app.StartAsync();
        return app;
    }

    static async Task Fail(HttpContext ctx, string message)
    {
        ctx.Response.StatusCode = 400;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { error = message }));
    }
}
