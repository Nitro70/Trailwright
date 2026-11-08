using Trailwright.Format;

namespace Trailwright.Model;

/// <summary>
/// A small software renderer for blueprint pictures: every occupied cell is drawn as a shaded cube in
/// an isometric view, painter's order, on a transparent 512x512 canvas. The transparent background is
/// also where the game-format data is stored, so a render always leaves plenty of room.
/// </summary>
public static class Thumbnail
{
    public static RgbaImage Render(Build build, int size = 512)
    {
        var img = new RgbaImage(size, size);
        var cells = new Dictionary<Int3, (byte R, byte G, byte B)>();
        foreach (var b in build.Blocks)
        {
            var (c1, _) = BuildEditor.GetColor(b);
            var hex = c1 is int i && i >= 0 && i < build.Catalog.Palette.Count ? build.Catalog.Palette[i] : "#9e9e9e";
            var col = (Convert.ToByte(hex[1..3], 16), Convert.ToByte(hex[3..5], 16), Convert.ToByte(hex[5..7], 16));
            foreach (var c in Geometry.Cells(b)) cells.TryAdd(c, col);
        }
        if (cells.Count == 0) return img;

        // view from +X, +Y, -Z; screen x = (x + z) * cos30, screen y = (x - z) * sin30 - y
        const double C = 0.8660254, S = 0.5;
        (double X, double Y) P(double x, double y, double z) => ((x + z) * C, (x - z) * S - y);
        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        foreach (var c in cells.Keys)
            foreach (var dx in new[] { -0.5, 0.5 }) foreach (var dy in new[] { -0.5, 0.5 }) foreach (var dz in new[] { -0.5, 0.5 })
            {
                var p = P(c.X + dx, c.Y + dy, c.Z + dz);
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X); minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
            }
        double scale = Math.Min((size * 0.8) / Math.Max(maxX - minX, 1e-6), (size * 0.8) / Math.Max(maxY - minY, 1e-6));
        double ox = size / 2.0 - (minX + maxX) / 2 * scale, oy = size / 2.0 - (minY + maxY) / 2 * scale;

        // faces visible from the camera: +X (right), +Y (top), -Z (left)
        var faces = new (Int3 N, double Shade, (double, double, double)[] Corners)[]
        {
            (new Int3(0, 1, 0), 1.00, [(-.5, .5, -.5), (.5, .5, -.5), (.5, .5, .5), (-.5, .5, .5)]),
            (new Int3(1, 0, 0), 0.78, [(.5, -.5, -.5), (.5, .5, -.5), (.5, .5, .5), (.5, -.5, .5)]),
            (new Int3(0, 0, -1), 0.62, [(-.5, -.5, -.5), (.5, -.5, -.5), (.5, .5, -.5), (-.5, .5, -.5)]),
        };
        // far to near along the view direction (1, 1, -1)
        foreach (var c in cells.Keys.OrderBy(c => c.X + c.Y - c.Z))
        {
            var col = cells[c];
            foreach (var (n, shade, corners) in faces)
            {
                if (cells.ContainsKey(c + n)) continue;
                var poly = corners.Select(k => { var p = P(c.X + k.Item1, c.Y + k.Item2, c.Z + k.Item3); return (p.X * scale + ox, p.Y * scale + oy); }).ToArray();
                FillQuad(img, poly, (byte)(col.R * shade), (byte)(col.G * shade), (byte)(col.B * shade));
            }
        }
        return img;
    }

    static void FillQuad(RgbaImage img, (double X, double Y)[] q, byte r, byte g, byte b)
    {
        int x0 = Math.Max(0, (int)Math.Floor(q.Min(p => p.X))), x1 = Math.Min(img.Width - 1, (int)Math.Ceiling(q.Max(p => p.X)));
        int y0 = Math.Max(0, (int)Math.Floor(q.Min(p => p.Y))), y1 = Math.Min(img.Height - 1, (int)Math.Ceiling(q.Max(p => p.Y)));
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                double px = x + 0.5, py = y + 0.5;
                bool pos = false, neg = false;
                for (int i = 0; i < 4; i++)
                {
                    var a = q[i]; var c = q[(i + 1) % 4];
                    double cross = (c.X - a.X) * (py - a.Y) - (c.Y - a.Y) * (px - a.X);
                    if (cross > 1e-9) pos = true; else if (cross < -1e-9) neg = true;
                }
                if (pos && neg) continue;
                int o = (y * img.Width + x) * 4;
                img.Pixels[o] = r; img.Pixels[o + 1] = g; img.Pixels[o + 2] = b; img.Pixels[o + 3] = 255;
            }
    }
}
