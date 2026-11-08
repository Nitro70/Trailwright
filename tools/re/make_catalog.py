"""Generate src/Trailwright.Core/Data/catalog.json from the installed game (read-only).

Per block type: GUID, English name, inventory categories, grid box, attachment knobs per part,
costs, the default saved node(s) the game ships for it (StructureGraphBlob, protobuf, base64),
and its slider settings: label, in-game min/max (evaluated from the game's own
GetMinMaxSliderValues on the prefab's values) and display multiplier.

Also writes the paint palette, key action names (EAction) and category names.

    python make_catalog.py [out.json]
"""
import base64
import json
import re
import struct
import sys

import lz4.frame
import UnityPy

import miniemu as E
import tmasset as A
import tmcode as C
import tmmeta as M
import tmtypes as TT

GAME = M.GAME
BUNDLE = GAME + "/Trailmakers_Data/StreamingAssets/AssetBundles/blocks"
for i, im in enumerate(M.images):
    if im.name in ("Assembly-CSharp.dll",):
        C.index_image(i)

VEC_LEN = {"UnityEngine.Vector2": 2, "UnityEngine.Vector3": 3, "UnityEngine.Vector4": 4, "UnityEngine.Color": 4}


def enum_map(full):
    t = [x for x in M.tdefs if x.full() == full][0]
    return M.enum_values(t)


def chain(t):
    out = []
    while t is not None and t.full() not in A.STOP:
        out.append(t)
        t = A.parent_td(t)
    return out


def layout(t):
    """memory offset -> (field name, type name) for instance fields of t and its bases."""
    out = {}
    for c in chain(t):
        offs = TT.field_offsets(c.index)
        for k, o in enumerate(offs):
            n, ti, tok = M.field(c.field_start + k)
            va = TT._type_ptrs[ti]
            _d, _k, bits = TT.read_type_at(va)
            if bits & 0x10:
                continue
            out.setdefault(o, (n, TT.name_type_va(va)))
    return out


def make_reader(t, values):
    lay = layout(t)

    def reader(off, size):
        for base in range(off, off - 16, -4):
            if base in lay:
                name, tname = lay[base]
                v = values.get(name)
                comp = (off - base) // 4
                if v is None:
                    return None
                if isinstance(v, list) and tname in VEC_LEN:
                    return E.f2i(v[comp]) if comp < len(v) else None
                if comp != 0:
                    return None
                if isinstance(v, bool):
                    return int(v)
                if isinstance(v, float):
                    return E.f2i(v)
                if isinstance(v, int):
                    return v & 0xFFFFFFFF
                if isinstance(v, dict):
                    return ("OBJ", name)
                return None
        return None
    return reader, lay


def load_string_index():
    """(CAB name, path id) -> English text of every LocalizedStringRef in the block and asset-pack bundles."""
    import os
    base = GAME + "/Trailmakers_Data/StreamingAssets/AssetBundles/"
    paths = [base + "blocks"] + [os.path.join(base, "worldassetpacks", f) for f in os.listdir(base + "worldassetpacks")
                                 if not f.endswith(".dependencies")]
    out = {}
    for p in paths:
        try:
            e = UnityPy.load(p)
        except Exception:
            continue
        for bf in e.files.values():
            for cab, sf in getattr(bf, "files", {}).items():
                if cab.endswith(".resS") or not hasattr(sf, "objects"):
                    continue
                for pid, o in sf.objects.items():
                    if o.type.name != "MonoBehaviour":
                        continue
                    try:
                        d = A.read_monobehaviour(o.get_raw_data(), "LocalizedStringRef")
                    except Exception:
                        continue
                    g = d.get("m_originalGuid", "")
                    if d["__consumed"] == d["__size"] and len(g) == 32 and all(ch in "0123456789abcdef" for ch in g):
                        out[(cab, pid)] = d.get("m_mainLanguageText") or None
    return out


def pretty_field(name, i):
    m = re.match(r"Slider(\d)_?(.*?)_?Header", name)
    if m and m.group(2):
        return re.sub(r"(?<!^)(?=[A-Z])", " ", m.group(2))
    m = re.match(r"(.*?)SliderHeader", name)
    if m and m.group(1):
        return re.sub(r"(?<!^)(?=[A-Z])", " ", m.group(1))
    return "Setting %d" % (i + 1)


def find_method(t, name):
    for c in chain(t):
        for k in range(c.method_count):
            mi = c.method_start + k
            if M.method(mi)[0] == name:
                return C.method_va(mi)
    return None


def evaluate(va, idx, reader):
    try:
        return E.run(va, idx, reader)
    except (E.GiveUp, KeyError, IndexError, TypeError, struct.error):
        return None


def header_field(va, idx, reader, lay):
    """The first instance field GetSliderHeader reads for this index (it then calls ToString)."""
    loads = []

    def spy(off, size):
        loads.append(off)
        r = reader(off, size)
        return r if r is not None else ("OBJ", off)
    try:
        E.run(va, idx, spy)
    except (E.GiveUp, KeyError, IndexError, TypeError, struct.error):
        pass
    for off in loads:
        if off in lay and "eader" in lay[off][0]:
            return lay[off][0]
    return None


def main(out):
    env = UnityPy.load(BUNDLE)
    scripts = {o.path_id: o.read() for o in env.objects if o.type.name == "MonoScript"}
    cls = {pid: (s.m_Namespace + "." if s.m_Namespace else "") + s.m_ClassName for pid, s in scripts.items()}
    mbs = {}
    for o in env.objects:
        if o.type.name == "MonoBehaviour":
            mbs[o.path_id] = (o, cls.get(o.read(check_read=False).m_Script.m_PathID))

    loc_cache = {}
    main_cab = [k for k in list(env.files.values())[0].files if not k.endswith(".resS")][0]
    externals = [e.path.split("/")[-1] for e in list(env.files.values())[0].files[main_cab].externals]
    strings = load_string_index()

    def loc_text(ls):
        if not isinstance(ls, dict):
            return None
        ref = ls.get("LocalizedStringRef")
        if ref and ref[1] > 0 and ref[1] - 1 < len(externals):
            t = strings.get((externals[ref[1] - 1], ref[2]))
            if t:
                return t
        if ref and ref[1] == 0:
            t = strings.get((main_cab, ref[2]))
            if t:
                return t
        s = ls.get("DevOnly_DefaultString")
        if s:
            return s
        if ref and ref[1] == 0 and ref[2] in mbs:
            pid = ref[2]
            if pid not in loc_cache:
                o, c = mbs[pid]
                try:
                    d = A.read_monobehaviour(o.get_raw_data(), c)
                    loc_cache[pid] = d.get("m_mainLanguageText") or None
                except Exception:
                    loc_cache[pid] = None
            return loc_cache[pid]
        return None

    behaviours = {}
    for pid, (o, c) in mbs.items():
        if c != "Block":
            continue
        b = A.read_monobehaviour(o.get_raw_data(), "Block")
        mref, bref = b.get("m_metadata"), b.get("m_attachedBlockBehaviour")
        if not (mref and mref[1] == 0 and bref and bref[1] == 0 and bref[2] in mbs):
            continue
        bo, bc = mbs[bref[2]]
        try:
            vals = A.read_monobehaviour(bo.get_raw_data(), bc)
        except Exception:
            vals = None
        behaviours.setdefault(mref[2], (bc, vals))

    blocks = []
    unknown_ranges = 0
    for pid, (o, c) in mbs.items():
        if c != "BlockMetadata":
            continue
        d = A.read_monobehaviour(o.get_raw_data(), "BlockMetadata")
        gb = d["GridBounds"]
        entry = {
            "guid": d["m_guid"], "name": loc_text(d["BlockName"]) or d["m_Name"], "asset": d["m_Name"],
            "category": d["m_inventoryTag"], "categories": d["m_newInventoryTags"], "subCategory": d["m_newInventorySubTag"],
            "sort": d["m_newInventorySortOrder"],
            "gridMin": [gb["SubGrid_LowerBackLeft"][k] for k in "XYZ"],
            "gridMax": [gb["SubGrid_UpperFrontRight"][k] for k in "XYZ"],
            "credits": d["creditsCost"], "powerCores": d["powerCoreValue"], "complexity": d["complexityValue"],
            "tintable": bool(d["isTintable"]), "secondaryColor": bool(d["hasSecondaryColor"]),
            "knobs": [[[*[k["DoubleGridPosition"][a] for a in "XYZ"], *[k["DoubleGridNormal"][a] for a in "XYZ"]]
                       for k in part["Knobs"]] for part in d["KnobsPerPart"]],
            "template": base64.b64encode(lz4.frame.decompress(d["StructureGraphBlob"])).decode() if d["StructureGraphBlob"] else None,
            "behaviour": None, "sliders": [],
        }
        if pid in behaviours:
            bc, vals = behaviours[pid]
            entry["behaviour"] = bc
            t = A._td_by_name(bc)
            if vals and t is not None:
                reader, lay = make_reader(t, vals)
                mm = find_method(t, "GetMinMaxSliderValues")
                cnt_va = find_method(t, "GetSliderCount")
                unit_va = find_method(t, "GetSliderUnitMultiplier")
                hdr_va = find_method(t, "GetSliderHeader")
                count = None
                if cnt_va:
                    r = evaluate(cnt_va, 0, reader)
                    if r and isinstance(r["rax"], int):
                        count = r["rax"] & 0xFFFFFFFF
                if mm:
                    n = count if count is not None and count <= 5 else 5
                    for i in range(n):
                        r = evaluate(mm, i, reader)
                        s = {"index": i}
                        if r and isinstance(r["rax"], int):
                            lo, hi = E.i2f(r["rax"] & 0xFFFFFFFF), E.i2f(r["rax"] >> 32)
                            s["min"], s["max"] = round(lo, 6), round(hi, 6)
                        elif count is None:
                            break  # past the last slider (index out of range) or unknown
                        else:
                            unknown_ranges += 1
                        if unit_va:
                            u = evaluate(unit_va, i, reader)
                            if u and isinstance(u["xmm0"], int):
                                s["unit"] = round(E.i2f(u["xmm0"]), 6)
                        if hdr_va:
                            hf = header_field(hdr_va, i, reader, lay)
                            if hf:
                                s["label"] = loc_text(vals.get(hf)) or pretty_field(hf, i)
                        entry["sliders"].append(s)
        blocks.append(entry)

    blocks.sort(key=lambda b: (b["name"] or "", b["guid"]))
    palette = json.load(open("../decompile/out/palette.json")) if False else None
    from palette_extract import pal as palette  # noqa: E402  (re-extracted from the game each run)
    cat = {
        "source": "Generated by tools/re/make_catalog.py from the installed Trailmakers game files.",
        "metadataVersion": M.VERSION,
        "palette": palette,
        "actions": {str(v): k for k, v in enum_map("EAction").items()},
        "categoryNames": {str(v): k for k, v in enum_map("InventoryView/BlockCategory").items()},
        "inventoryCategoryNames": {str(v): k for k, v in enum_map("InventoryView/InventoryCategory").items()},
        "subCategoryNames": {str(v): k for k, v in enum_map("InventoryView/InventorySubCategory").items()},
        "blocks": blocks,
    }
    json.dump(cat, open(out, "w", encoding="utf-8"), indent=0, ensure_ascii=False)
    with_sl = sum(1 for b in blocks if b["sliders"])
    print("blocks %d, with sliders %d, unknown ranges %d" % (len(blocks), with_sl, unknown_ranges))


if __name__ == "__main__":
    sys.argv = sys.argv[:1] + sys.argv[1:]
    main(sys.argv[1] if len(sys.argv) > 1 else "catalog.json")
