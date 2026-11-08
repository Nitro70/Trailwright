"""Extract SerializableColor.KNOWN_COMMON_COLORS (the in-game paint palette) from the cctor.
Each entry is built from a packed 0xAABBGGRR immediate; index i in the palette is stored in
blueprints as IndexedColor.m_commonColorIndexOffByOne = i + 1. Writes palette.json."""
import json, re, sys
import tmcode as C

(full, mi, va), = C.find('SerializableColor', '.cctor')
vals = [int(m.group(1), 16) for m in re.finditer(r"mov    dword ptr \[rbp \+ 0x18\], (0x[0-9a-f]+)", C.disasm(va, 0x2000))]
pal = ["#%02x%02x%02x" % (v & 0xFF, (v >> 8) & 0xFF, (v >> 16) & 0xFF) for v in vals]
if __name__ == "__main__":
    print(len(pal), pal)
    json.dump(pal, open(sys.argv[1] if len(sys.argv) > 1 else "palette.json", "w"))
