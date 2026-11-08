"""Find every block's render meshes in the game's bundles (read-only, used by export_models.py).

Block prefabs live in the `blocks` bundle and (for about a hundred newer blocks) in the large
`prefabs` bundle; materials are in `trailmakersshaders`. Every reference is resolved relative to the
file the referring object came from, so prefabs in either bundle work the same way.
"""
import re

import numpy as np
import UnityPy

import tmasset as A

BASE = A.M.GAME + "/Trailmakers_Data/StreamingAssets/AssetBundles/"


def local_matrix(tr):
    q = tr.m_LocalRotation
    x, y, z, w = q.x, q.y, q.z, q.w
    R = np.array([
        [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
        [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
        [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)],
    ])
    s = tr.m_LocalScale
    M = np.eye(4)
    M[:3, :3] = R @ np.diag([s.x, s.y, s.z])
    t = tr.m_LocalPosition
    M[:3, 3] = [t.x, t.y, t.z]
    return M


class Bundles:
    SKIP_INACTIVE = r"(Punctured|WW2|WWII|Broken|Garbage|Damaged|Gizmo|BuildBounds|Particle|Emitter|Flame|_Off$|Canvas)"

    def __init__(self):
        # blocks: prefabs, most meshes and textures; trailmakersshaders: materials; prefabs: the rest
        self.env = UnityPy.load(BASE + "blocks", BASE + "trailmakersshaders", BASE + "prefabs")
        self.files = {}   # CAB name -> SerializedFile
        for bf in self.env.files.values():
            for cab, sf in getattr(bf, "files", {}).items():
                if hasattr(sf, "objects"):
                    self.files[cab.lower()] = sf

    # ---- references ----------------------------------------------------------------------------

    def deref(self, sf, pptr):
        """ObjectReader a PPtr points at, resolved against the file `sf` that holds the PPtr."""
        if not pptr.m_PathID:
            return None
        target = sf if pptr.m_FileID == 0 else self.files.get(sf.externals[pptr.m_FileID - 1].path.split("/")[-1].lower())
        if target is None:
            return ("external", sf.externals[pptr.m_FileID - 1].path, pptr.m_PathID)
        return target.objects.get(pptr.m_PathID)

    def script_class(self, sf, ob):
        sc = self.deref(sf, ob.read(check_read=False).m_Script)
        return sc.read().m_ClassName if sc is not None and not isinstance(sc, tuple) else None

    # ---- block prefabs -------------------------------------------------------------------------

    def blocks(self):
        """guid -> (SerializedFile, prefab root GameObject reader), from both prefab bundles."""
        out = {}
        for cab, sf in self.files.items():
            scripts = {}
            for pid, o in sf.objects.items():
                if o.type.name != "MonoBehaviour":
                    continue
                key = o.read(check_read=False).m_Script
                k = (key.m_FileID, key.m_PathID)
                if k not in scripts:
                    sc = self.deref(sf, key)
                    scripts[k] = sc.read().m_ClassName if sc is not None and not isinstance(sc, tuple) else None
                # plain prefabs carry a Block; newer blocks only have client/server prefab pairs,
                # and the client one is the one with the graphics
                if scripts[k] not in ("Block", "Block_Client"):
                    continue
                try:
                    b = A.read_monobehaviour(o.get_raw_data(), scripts[k])
                except Exception:
                    continue
                md = b["m_metadata"]
                mo = self.deref(sf, _P(md[1], md[2]))
                if mo is None or isinstance(mo, tuple):
                    continue
                try:
                    m = A.read_monobehaviour(mo.get_raw_data(), "BlockMetadata")
                except Exception:
                    continue
                if m["m_guid"] in out and scripts[k] != "Block":
                    continue
                go = sf.objects.get(b["m_GameObject"][1])
                if go is not None:
                    out[m["m_guid"]] = (sf, go)
        return out

    def children(self, sf, go_reader):
        """(transform reader, [(sf, child GameObject reader)])"""
        go = go_reader.read()
        tr = None
        for c in go.m_Component:
            p = c.component if hasattr(c, "component") else c
            ob = self.deref(sf, p)
            if ob is not None and not isinstance(ob, tuple) and ob.type.name == "Transform":
                tr = ob
        kids = []
        if tr is not None:
            for ch in tr.read().m_Children:
                cto = self.deref(sf, ch)
                if cto is None or isinstance(cto, tuple):
                    continue
                kids.append((sf, self.deref(sf, cto.read().m_GameObject)))
        return tr, kids

    def renderers(self, sf, go_reader, parent_matrix=None, include_inactive=False):
        """Yield (matrix relative to the start object, mesh or ('builtin', id), [materials]).
        Lower detail levels (*_LOD1...) and alternative skins (*_Skin) are left out."""
        go = go_reader.read()
        tr = None
        mesh = None
        mats = []
        for c in go.m_Component:
            p = c.component if hasattr(c, "component") else c
            ob = self.deref(sf, p)
            if ob is None or isinstance(ob, tuple):
                continue
            tn = ob.type.name
            if tn == "Transform":
                tr = ob.read()
            elif tn == "MeshFilter":
                mp = ob.read().m_Mesh
                if not mp.m_PathID:
                    continue
                if mp.m_FileID and "unity default resources" in sf.externals[mp.m_FileID - 1].path.lower():
                    mesh = ("builtin", mp.m_PathID)
                else:
                    mo = self.deref(sf, mp)
                    mesh = mo.read() if mo is not None and not isinstance(mo, tuple) else None
            elif tn == "MeshRenderer":
                r = ob.read()
                if not getattr(r, "m_Enabled", True):
                    continue
                for mp in r.m_Materials:
                    mo = self.deref(sf, mp)
                    mats.append(mo.read() if mo is not None and not isinstance(mo, tuple) else None)
        if tr is None:
            return
        W = np.eye(4) if parent_matrix is None else parent_matrix @ local_matrix(tr)
        name = go.m_Name
        if mesh is not None and mats and not re.search(r"LOD0*[1-9]", name, re.I) and not re.search(r"_Skin$", name, re.I):
            yield W, mesh, mats
        for ch in tr.m_Children:
            cto = self.deref(sf, ch)
            if cto is None or isinstance(cto, tuple):
                continue
            cg = self.deref(sf, cto.read().m_GameObject)
            if cg is None or isinstance(cg, tuple):
                continue
            g = cg.read()
            if not getattr(g, "m_IsActive", True):
                # switched off in the prefab: a variant the game switches on at runtime (an engine's
                # default look) or a damage/alternative version: only on request, never the latter
                if not include_inactive or re.search(self.SKIP_INACTIVE, g.m_Name, re.I):
                    continue
            yield from self.renderers(sf, cg, W, include_inactive)


class _P:
    """Minimal PPtr stand-in for references parsed by tmasset (fileID, pathID tuples)."""

    def __init__(self, f, p):
        self.m_FileID, self.m_PathID = f, p
