"""Static analysis of every slider block behaviour (read-only).

For each class implementing ISliderBlockBehaviour:
  * GetMinMaxSliderValues / GetSliderUnitMultiplier / GetSliderCount: float constants and the
    instance fields they read (named via the binary's field offset table)
  * Internal_ApplyBlockBehaviourSetup (the code that runs when a saved blueprint is applied):
    every load of SliderBlockSetup.sliderValueN (+0x78..+0x88) and whether a comparison/min/max/
    Mathf.Clamp follows it within a few instructions (= the game clamps that slider on load).

Writes slider_analysis.json and a readable slider_analysis.txt next to it.
"""
import json
import re
import struct
import sys

import tmcode as C
import tmmeta as M
import tmtypes as TT
import tmasset as A

SLIDER_OFF = {0x78: 0, 0x7C: 1, 0x80: 2, 0x84: 3, 0x88: 4}

for i, im in enumerate(M.images):
    if im.name in ("Assembly-CSharp.dll", "mscorlib.dll", "UnityEngine.CoreModule.dll"):
        C.index_image(i)


def f32_at(va):
    off = TT.raw_of_va(va)
    return struct.unpack_from("<f", TT.blob, off)[0] if off is not None else None


def chain(t):
    out = []
    while t is not None and t.full() not in A.STOP:
        out.append(t)
        t = A.parent_td(t)
    return out


def field_names_by_offset(t):
    names = {}
    for c in chain(t):
        offs = TT.field_offsets(c.index)
        for k, o in enumerate(offs):
            n, ti, tok = M.field(c.field_start + k)
            va = TT._type_ptrs[ti]
            _d, _k, bits = TT.read_type_at(va)
            if bits & 0x10:  # static
                continue
            names.setdefault(o, "%s.%s" % (c.name, n))
    return names


def methods_of(t, wanted):
    out = []
    for c in chain(t):
        for k in range(c.method_count):
            mi = c.method_start + k
            n = M.method(mi)[0]
            if n in wanted:
                out.append((c.name, n, mi))
    return out


def annotate(va, fields, maxlen=0x1200):
    text = C.disasm(va, maxlen)
    lines = []
    for l in text.splitlines():
        if "lock or" in l or "0x180716e80" in l:
            continue
        m = re.search(r"\[rip \+ 0x[0-9a-f]+\]\s+;\s+\[(0x[0-9a-f]+)\]", l)
        if m and "movss" in l or m and "comiss" in l or m and ("mulss" in l or "minss" in l or "maxss" in l or "divss" in l or "addss" in l or "subss" in l):
            v = f32_at(C.BASE + int(m.group(1), 16))
            l += "  {%r}" % v
        m = re.search(r"\[(r[a-z0-9]+) \+ (0x[0-9a-f]+)\]", l)
        if m and "rip" not in m.group(1) and "rsp" not in m.group(1) and "rbp" not in m.group(1):
            off = int(m.group(2), 16)
            tag = []
            if off in fields:
                tag.append("this?" + fields[off])
            if off in SLIDER_OFF:
                tag.append("setup?.slider%d" % (SLIDER_OFF[off] + 1))
            if tag:
                l += "  <" + " | ".join(tag) + ">"
        lines.append(l)
    return lines


def clamp_report(lines):
    """For each slider load in apply code: follow the xmm register until it is stored into a field
    or overwritten. A comparison against something else (comiss/minss/maxss, not the NaN self-test
    'ucomiss x, x') or a Mathf.Clamp call on the way means the game clamps that slider on load."""
    res = []
    for i, l in enumerate(lines):
        m = re.search(r"movss\s+(xmm\d+), dword ptr \[r[a-z0-9]+ \+ (0x[0-9a-f]+)\]", l)
        if not m or "setup?.slider" not in l:
            continue
        reg, off = m.group(1), int(m.group(2), 16)
        consts, clamped, stored = [], False, None
        for w in lines[i + 1:i + 40]:
            cm = re.search(r"(u?comiss|minss|maxss)\s+(xmm\d+), (xmm\d+|dword ptr .*)", w)
            if cm and reg in w and not (cm.group(1) == "ucomiss" and cm.group(2) == cm.group(3)):
                clamped = True
            c = re.search(r"\{(.+?)\}", w)
            if c:
                consts.append(c.group(1))
            if "Clamp" in w:
                clamped = True
            sm = re.search(r"movss\s+dword ptr \[r[a-z0-9]+ \+ 0x[0-9a-f]+\], " + reg + r"", w)
            if sm:
                stored = w.split("<")[-1].rstrip(">") if "<" in w else "?"
                break
            if re.search(r"movss\s+" + reg + r", ", w) or re.search(r"movaps\s+" + reg + r", ", w):
                # overwritten (e.g. by a clamp bound) - keep scanning: clamps often load the bound here
                pass
        res.append({"slider": SLIDER_OFF[off] + 1, "clamped": clamped, "consts": consts[:6], "stored_to": stored,
                    "at": l.split(":")[0].strip()})
    return res


def main(out_base):
    iface = [x for x in M.tdefs if x.name == "ISliderBlockBehaviour"][0]
    IF_OFF, _, IF_N = M.sec(16)
    ifs = struct.unpack_from("<%di" % IF_N, M.md, IF_OFF)
    impl = []
    for t in M.tdefs:
        if t.interfaces_count and t.interfaces_start >= 0:
            for k in range(t.interfaces_count):
                d, kind, _ = TT.read_type_at(TT._type_ptrs[ifs[t.interfaces_start + k]])
                if kind in (0x11, 0x12) and d == iface.index:
                    impl.append(t)
    report = {}
    text = []
    for t in impl:
        fields = field_names_by_offset(t)
        entry = {"class": t.full(), "methods": {}}
        text.append("#" * 100 + "\n# " + t.full())
        for cname, mname, mi in methods_of(t, {"GetMinMaxSliderValues", "GetSliderUnitMultiplier", "GetSliderCount",
                                                  "GetSimpleSliderCount", "Internal_ApplyBlockBehaviourSetup",
                                                  "GetDefaultBlockBehaviourSetup", "GetSliderFormat"}):
            if cname != t.name and mname != "Internal_ApplyBlockBehaviourSetup":
                pass
            va = C.method_va(mi)
            if not va:
                continue
            lines = annotate(va, fields, 0x1800 if "Apply" in mname or "Default" in mname else 0x900)
            text.append("== %s.%s @%#x" % (cname, mname, va - C.BASE))
            text.extend(lines)
            if mname == "Internal_ApplyBlockBehaviourSetup" and cname == t.name:
                entry["apply_clamps"] = clamp_report(lines)
            if mname == "GetMinMaxSliderValues":
                entry["minmax_consts"] = [re.search(r"\{(.+?)\}", l).group(1) for l in lines if "{" in l]
                entry["minmax_fields"] = sorted(set(re.findall(r"this\?([A-Za-z0-9_.]+)", "\n".join(lines))))
            if mname == "GetSliderUnitMultiplier":
                entry["unit_consts"] = [re.search(r"\{(.+?)\}", l).group(1) for l in lines if "{" in l]
        report[t.full()] = entry
    json.dump(report, open(out_base + ".json", "w"), indent=1)
    open(out_base + ".txt", "w").write("\n".join(text))
    for k, v in sorted(report.items()):
        print("%-38s apply=%s" % (k, [(c["slider"], "CLAMP" if c["clamped"] else "free", c["stored_to"]) for c in v.get("apply_clamps", [])]))


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "../decompile/out/slider_analysis")
