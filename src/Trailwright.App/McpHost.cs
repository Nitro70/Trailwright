using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace Trailwright.App;

/// <summary>The AI port: an MCP server (streamable HTTP) on 127.0.0.1 that only exposes the builder.</summary>
public sealed class McpHost(AppState state)
{
    WebApplication? _app;
    readonly SemaphoreSlim _gate = new(1, 1);

    public bool Running => _app != null;
    public string? Error { get; private set; }
    public string? Url => Running ? $"http://127.0.0.1:{state.Settings.AiPort}/mcp" : null;

    public const string Instructions = """
        Trailwright builder: you are editing the Trailmakers vehicle that is open in the Trailwright app.
        You can only touch the builder (blocks, colours, block settings, keys, name, undo, save); the user's
        blueprint library and the app's options are off limits.

        Coordinates: integer grid cells, 1 cell = 0.25 m. +X is right, +Y is up, +Z is forward (the way a
        seat faces with no rotation). Rotations are quarter turns (or degrees, multiples of 90) about X, Y and Z,
        applied Z first, then X, then Y, like Unity.

        Blocks only connect where both blocks have an attachment point on the touching faces. Placement fails
        when cells are taken or the block would not attach (pass allow_floating only on purpose).

        A good loop: builder_status -> list_block_types (find parts) -> block_type_info (size, attach points,
        settings) -> check_placement or find_attach_spots -> place_block / place_blocks -> layer_map or
        get_blocks to verify -> save_build when the user wants it in the game.
        A drivable car needs a seat (category SEAT), a frame, wheels with power (engines/motors or powered
        wheels) and usually steering hinges; planes need thrust, lift and a seat.

        Block settings (speed, strength, power...) are capped to the game's own ranges unless the user has
        ticked "Unrestricted" in the app. You cannot change that switch. Note: the game itself clamps most
        settings to the in-game range when it loads a blueprint.
        """;

    public async Task SetEnabled(bool enabled)
    {
        await _gate.WaitAsync();
        try
        {
            await StopInternal();
            Error = null;
            if (!enabled) return;
            try { await StartInternal(); }
            catch (Exception ex) { Error = ex.InnerException?.Message ?? ex.Message; }
        }
        finally { _gate.Release(); }
    }

    async Task StartInternal()
    {
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls($"http://127.0.0.1:{state.Settings.AiPort}");
            builder.Services.AddSingleton(state);
            builder.Services.AddMcpServer(o =>
                {
                    o.ServerInfo = new Implementation { Name = "trailwright", Version = "0.1.0" };
                    o.ServerInstructions = Instructions;
                })
                .WithHttpTransport(o => o.Stateless = true)
                .WithTools<BuilderTools>(AppState.Json);
            var app = builder.Build();
            // Loopback only, and no browser pages from other sites (DNS rebinding / cross-site requests).
            app.Use(async (ctx, next) =>
            {
                if (ctx.Request.Host.Host is not ("127.0.0.1" or "localhost")) { ctx.Response.StatusCode = 421; return; }
                var origin = ctx.Request.Headers.Origin.ToString();
                if (origin.Length > 0 && !(Uri.TryCreate(origin, UriKind.Absolute, out var o) && o.IsLoopback))
                {
                    ctx.Response.StatusCode = 403;
                    return;
                }
                await next();
            });
            app.MapMcp("/mcp");
            app.MapGet("/", () => "Trailwright AI port. Connect an MCP client to /mcp (streamable HTTP).");
            try
            {
                await app.StartAsync();
                _app = app;
            }
            catch (Exception ex)
            {
                Error = ex.InnerException?.Message ?? ex.Message;
                await app.DisposeAsync();
            }
        }
    }

    async Task StopInternal()
    {
        if (_app == null) return;
        var a = _app;
        _app = null;
        try { using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await a.StopAsync(cts.Token); } catch { }
        await a.DisposeAsync();
    }
}
