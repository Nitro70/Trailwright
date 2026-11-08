namespace Trailwright.Model;

public readonly record struct Int3(int X, int Y, int Z)
{
    public static Int3 operator +(Int3 a, Int3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Int3 operator -(Int3 a, Int3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Int3 operator -(Int3 a) => new(-a.X, -a.Y, -a.Z);
    public static Int3 operator *(Int3 a, int k) => new(a.X * k, a.Y * k, a.Z * k);
    public static Int3 Min(Int3 a, Int3 b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
    public static Int3 Max(Int3 a, Int3 b) => new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));
    public int[] ToArray() => [X, Y, Z];
    public override string ToString() => $"({X},{Y},{Z})";
}

/// <summary>
/// A quarter-turn rotation. Blueprints store Euler angles in quarter turns (EulerX/Y/Z = 0..3);
/// Unity builds the rotation as Ry * Rx * Rz (ZXY order), which is what reproduces the game's
/// collision-free grid layouts.
/// </summary>
public readonly struct Rot : IEquatable<Rot>
{
    readonly int[] _m; // row-major 3x3, entries -1/0/1

    public int Ex { get; }
    public int Ey { get; }
    public int Ez { get; }

    Rot(int[] m, int ex, int ey, int ez) { _m = m; Ex = ex; Ey = ey; Ez = ez; }

    public static readonly Rot Identity = FromEuler(0, 0, 0);

    static int[] Axis(char axis, int q)
    {
        int c = new[] { 1, 0, -1, 0 }[((q % 4) + 4) % 4], s = new[] { 0, 1, 0, -1 }[((q % 4) + 4) % 4];
        return axis switch
        {
            'x' => [1, 0, 0, 0, c, -s, 0, s, c],
            'y' => [c, 0, s, 0, 1, 0, -s, 0, c],
            _ => [c, -s, 0, s, c, 0, 0, 0, 1],
        };
    }

    static int[] Mul(int[] a, int[] b)
    {
        var r = new int[9];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                r[i * 3 + j] = a[i * 3] * b[j] + a[i * 3 + 1] * b[3 + j] + a[i * 3 + 2] * b[6 + j];
        return r;
    }

    public static Rot FromEuler(int x, int y, int z)
    {
        x = ((x % 4) + 4) % 4; y = ((y % 4) + 4) % 4; z = ((z % 4) + 4) % 4;
        return new Rot(Mul(Axis('y', y), Mul(Axis('x', x), Axis('z', z))), x, y, z);
    }

    /// <summary>All 24 distinct rotations, each with the Euler triple the game itself tends to write (X never 2).</summary>
    public static readonly IReadOnlyList<Rot> All = BuildAll();

    static List<Rot> BuildAll()
    {
        var list = new List<Rot>();
        foreach (var x in new[] { 0, 1, 3, 2 })
            for (int y = 0; y < 4; y++)
                for (int z = 0; z < 4; z++)
                {
                    var r = FromEuler(x, y, z);
                    if (!list.Any(o => o.SameMatrix(r))) list.Add(r);
                }
        return list;
    }

    public bool SameMatrix(Rot o) => _m.AsSpan().SequenceEqual(o._m);

    /// <summary>The canonical Euler triple for this rotation's matrix.</summary>
    public Rot Canonical() { var me = this; return All.First(r => r.SameMatrix(me)); }

    public Int3 Apply(Int3 v) => new(
        _m[0] * v.X + _m[1] * v.Y + _m[2] * v.Z,
        _m[3] * v.X + _m[4] * v.Y + _m[5] * v.Z,
        _m[6] * v.X + _m[7] * v.Y + _m[8] * v.Z);

    public Rot Then(Rot outer) => new Rot(Mul(outer._m, _m), 0, 0, 0).Canonical();

    public int[] Matrix => (int[])_m.Clone();

    public bool Equals(Rot o) => SameMatrix(o);
    public override bool Equals(object? obj) => obj is Rot r && Equals(r);
    public override int GetHashCode() => HashCode.Combine(_m[0], _m[1], _m[2], _m[3], _m[4], _m[5], _m[6], _m[7]);
    public override string ToString() => $"euler({Ex * 90},{Ey * 90},{Ez * 90})";
}
