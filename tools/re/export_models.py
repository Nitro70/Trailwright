"""Extract block models from YOUR Trailmakers install into Trailwright's local model cache.

    python tools/re/export_models.py [out_dir]     (default %LOCALAPPDATA%\\Trailwright\\models)

Needs Python 3 with UnityPy and numpy. Set TRAILMAKERS_DIR if the game is not in the default
Steam folder. Nothing extracted here is ever put in the repository: the cache stays on this PC.

For every block prefab in the game's `blocks` bundle, each part ("Part 0", "Part 1"... = node
SocketIndex) is merged into one mesh in that part's own space (Unity metres, left-handed) with a
per-vertex colour and paint slot:
  * Trailmakers block shaders tint white vertices: vertex alpha < 128 takes the block's primary
    colour, >= 128 its secondary colour (checked against the game's own blueprint pictures).
  * Every other vertex keeps its baked colour.
File per part: {guid}_{part}.bin = "TWM1", u32 vertexCount, u32 indexCount, f32 pos[v*3],
f32 normal[v*3], u8 rgba[v*4] (a = paint slot 0 fixed / 1 primary / 2 secondary), u32 index[i].
Plus index.json: {guid: {parts: [part numbers], ...}}.
"""
import json
import os
import struct
import sys

import numpy as np
from UnityPy.helpers.MeshHelper import MeshHandler

import tmmodels as TM

SKIP_MATERIAL_WORDS = ("particle", "light_cone", "lightcone", "shadow", "outline", "highlight", "hologram")


def part_roots(B, sf, go_reader):
    """[(part number, GameObject reader)] from the prefab's direct 'Part N' children."""
    out = []
    _tr, kids = B.children(sf, go_reader)
    for ksf, cg in kids:
        if cg is None or isinstance(cg, tuple):
            continue
        name = cg.read().m_Name.strip()
        if name.startswith("Part ") and name[5:].isdigit():
            out.append((int(name[5:]), cg))
    return sorted(out, key=lambda t: t[0])


def builtin(pid):
    """Unity's built-in meshes from 'unity default resources', by their fixed path ids."""
    tris = []
    if pid == 10202:  # Cube, 1 m
        V, N = [], []
        for axis in range(3):
            for sign in (-1, 1):
                n = [0, 0, 0]; n[axis] = sign
                u, v = [(axis + 1) % 3, (axis + 2) % 3]
                base = len(V)
                for a, b in ((-1, -1), (1, -1), (1, 1), (-1, 1)):
                    p = [0.0, 0.0, 0.0]; p[axis] = 0.5 * sign; p[u] = 0.5 * a; p[v] = 0.5 * b
                    V.append(p); N.append(n)
                quad = [(0, 1, 2), (0, 2, 3)] if sign < 0 else [(0, 2, 1), (0, 3, 2)]
                tris += [(base + i, base + j, base + k) for i, j, k in quad]
        return np.array(V, np.float32), np.array(N, np.float32), None, [tris]
    if pid in (10206, 10207, 10208):  # Cylinder (r .5, h 2), Sphere (r .5), Capsule (r .5, h 2)
        seg, rings = 20, 12
        V, N = [], []
        for i in range(rings + 1):
            t = i / rings
            for j in range(seg + 1):
                ph = 2 * np.pi * j / seg
                if pid == 10206:
                    y = -1 + 2 * t; r = 0.5; n = [np.cos(ph), 0, np.sin(ph)]
                else:
                    th = np.pi * t
                    n = [np.sin(th) * np.cos(ph), -np.cos(th), np.sin(th) * np.sin(ph)]
                    r = 0.5 * np.sin(th); y = -0.5 * np.cos(th)
                    if pid == 10208:
                        y += -0.5 if t < 0.5 else 0.5
                V.append([r * np.cos(ph), y, r * np.sin(ph)]); N.append(n)
        for i in range(rings):
            for j in range(seg):
                a = i * (seg + 1) + j; b = a + seg + 1
                tris += [(a, a + 1, b), (b, a + 1, b + 1)]
        return np.array(V, np.float32), np.array(N, np.float32), None, [tris]
    if pid in (10209, 10210):  # Plane (10 m) / Quad (1 m)
        h = 5.0 if pid == 10209 else 0.5
        if pid == 10209:
            V = [[-h, 0, -h], [h, 0, -h], [h, 0, h], [-h, 0, h]]; N = [[0, 1, 0]] * 4
        else:
            V = [[-h, -h, 0], [h, -h, 0], [h, h, 0], [-h, h, 0]]; N = [[0, 0, -1]] * 4
        return np.array(V, np.float32), np.array(N, np.float32), None, [[(0, 2, 1), (0, 3, 2)]]
    return None


def vec3(values, count):
    """UnityPy gives per-vertex tuples of 3 or 4 components (normals are often x, y, z, w) or a flat
    list; return an (count, 3) float array, or None."""
    if not values or not count:
        return None
    a = np.array(values, dtype=np.float32)
    if a.ndim == 1:
        if len(a) % count:
            return None
        a = a.reshape(count, -1)
    if a.shape[0] != count or a.shape[1] < 3:
        return None
    return a[:, :3]


def mesh_arrays(mesh):
    if isinstance(mesh, tuple):
        return builtin(mesh[1])
    mh = MeshHandler(mesh)
    mh.process()
    if not mh.m_Vertices:
        return None
    V = vec3(mh.m_Vertices, mh.m_VertexCount)
    if V is None:
        return None
    n = len(V)
    N = vec3(mh.m_Normals, n)
    C = mh.m_Colors if mh.m_Colors and len(mh.m_Colors) >= n else None
    subs = mh.get_triangles()
    return V, N, C, subs


def colour(c):
    """(r,g,b,paint) bytes for one vertex colour as stored (0..255 ints or 0..1 floats)."""
    if c is None:
        return (200, 200, 200, 1)
    r, g, b, a = c
    if isinstance(r, float):
        r, g, b, a = (int(round(x * 255)) for x in (r, g, b, a))
    if min(r, g, b) >= 200:
        return (255, 255, 255, 1 if a < 128 else 2)
    return (r, g, b, 0)


def export_part(B, sf, part_go):
    P, Nn, Cc, Ii = [], [], [], []
    base = 0
    # the part's own transform is where the node sits; geometry goes into part space
    found = list(B.renderers(sf, part_go))
    if not found:  # e.g. an engine whose default look is switched on at runtime
        found = list(B.renderers(sf, part_go, include_inactive=True))
    for W, mesh, mats in found:
        names = " ".join((m.m_Name if m else "") for m in mats).lower()
        if any(w in names for w in SKIP_MATERIAL_WORDS):
            continue
        try:
            arr = mesh_arrays(mesh)
        except Exception as e:
            if os.environ.get("TW_DEBUG"):
                print("  mesh failed:", getattr(mesh, "m_Name", mesh), repr(e)[:160])
            continue
        if arr is None:
            continue
        V, N, C, subs = arr
        R = W[:3, :3]
        Vw = (V @ R.T) + W[:3, 3]
        if N is not None:
            Nw = N @ np.linalg.inv(R).T
            lens = np.linalg.norm(Nw, axis=1, keepdims=True)
            Nw = Nw / np.where(lens == 0, 1, lens)
        else:
            Nw = np.zeros_like(Vw)
        cols = [colour(C[i] if C is not None else None) for i in range(len(V))]
        tris = [t for k, sub in enumerate(subs) for t in sub]
        # mirrored transforms (negative scale) flip winding
        flip = np.linalg.det(R) < 0
        for a, b, c in tris:
            Ii.extend((base + a, base + c, base + b) if flip else (base + a, base + b, base + c))
        P.append(Vw); Nn.append(Nw); Cc.extend(cols)
        base += len(V)
    if base == 0:
        return None
    return np.concatenate(P).astype(np.float32), np.concatenate(Nn).astype(np.float32), np.array(Cc, dtype=np.uint8), np.array(Ii, dtype=np.uint32)


def write_bin(path, pos, nrm, col, idx):
    with open(path, "wb") as f:
        f.write(b"TWM1")
        f.write(struct.pack("<II", len(pos), len(idx)))
        f.write(pos.tobytes()); f.write(nrm.tobytes()); f.write(col.tobytes()); f.write(idx.tobytes())


def main(out):
    os.makedirs(out, exist_ok=True)
    B = TM.Bundles()
    prefabs = B.blocks()
    index = {}
    for i, (guid, (sf, go)) in enumerate(sorted(prefabs.items())):
        parts = part_roots(B, sf, go) or [(0, go)]
        done = []
        for num, pg in parts:
            try:
                r = export_part(B, sf, pg)
            except Exception as e:
                print("skip", guid, num, e)
                continue
            if r is None:
                continue
            pos, nrm, col, idx = r
            write_bin(os.path.join(out, f"{guid}_{num}.bin"), pos, nrm, col, idx)
            done.append({"part": num, "vertices": int(len(pos)), "min": pos.min(0).round(4).tolist(), "max": pos.max(0).round(4).tolist()})
        if done:
            index[guid] = {"name": go.read().m_Name, "parts": done}
        if i % 50 == 0:
            print(f"{i}/{len(prefabs)}")
    meta = os.path.join(TM.A.M.GAME, "Trailmakers_Data", "il2cpp_data", "Metadata", "global-metadata.dat")
    json.dump({"format": 1, "gameStamp": os.path.getmtime(meta) if os.path.exists(meta) else None, "blocks": index},
              open(os.path.join(out, "index.json"), "w"), indent=0)
    print("blocks with models:", len(index), "parts:", sum(len(v["parts"]) for v in index.values()), "->", out)


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.environ["LOCALAPPDATA"], "Trailwright", "models"))
