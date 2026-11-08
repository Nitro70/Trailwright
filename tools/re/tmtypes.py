"""Resolve Il2CppType indices (the ti=... numbers tmmeta prints) to readable type names.

Finds g_MetadataRegistration in GameAssembly.dll by its shape: fieldOffsetsCount and
typeDefinitionsSizesCount both equal the metadata's type definition count, two qwords
apart, each followed by a pointer. types/typesCount sit 32 bytes before them.
Read-only, on-disk files only.
"""
import struct
import pefile

import tmmeta as M

DLL = M.GAME + "/GameAssembly.dll"
pe = pefile.PE(DLL, fast_load=True)
BASE = pe.OPTIONAL_HEADER.ImageBase
blob = open(DLL, "rb").read()
SECS = [(s.Name.rstrip(b"\0").decode(), s.VirtualAddress, s.Misc_VirtualSize, s.PointerToRawData, s.SizeOfRawData)
        for s in pe.sections]


def raw_of_va(va):
    rva = va - BASE
    for _n, sva, vs, r, rs in SECS:
        if sva <= rva < sva + max(vs, rs):
            off = r + (rva - sva)
            return off if rva - sva < rs else None
    return None


def q(va):
    off = raw_of_va(va)
    return struct.unpack_from("<Q", blob, off)[0] if off is not None else None


def find_metadata_registration():
    n = M.TD_N
    for name, sva, vs, r, rs in SECS:
        if name not in (".data", ".rdata"):
            continue
        data = blob[r:r + rs]
        for off in range(0, len(data) - 128, 8):
            a, pa, b, pb = struct.unpack_from("<QQQQ", data, off)
            if a == n and b == n and BASE < pa < BASE + 0x10000000 and BASE < pb < BASE + 0x10000000:
                start = off - 80  # fieldOffsetsCount is at +80 in the struct
                vals = struct.unpack_from("<16Q", data, start)
                types_count, types_ptr = vals[6], vals[7]
                if 1000 < types_count < 1000000:
                    return BASE + sva + start, types_count, types_ptr
    raise RuntimeError("metadata registration not found")


MR_VA, TYPES_N, TYPES_PTR = find_metadata_registration()
_type_ptrs = [q(TYPES_PTR + 8 * i) for i in range(TYPES_N)]

PRIM = {1: "void", 2: "bool", 3: "char", 4: "sbyte", 5: "byte", 6: "short", 7: "ushort", 8: "int",
        9: "uint", 0xA: "long", 0xB: "ulong", 0xC: "float", 0xD: "double", 0xE: "string",
        0x18: "IntPtr", 0x19: "UIntPtr", 0x1C: "object"}


def read_type_at(va):
    off = raw_of_va(va)
    data, bits = struct.unpack_from("<QI", blob, off)
    return data, (bits >> 16) & 0xFF, bits


def name_type_va(va, depth=0):
    if depth > 8:
        return "..."
    data, kind, bits = read_type_at(va)
    if kind in PRIM:
        return PRIM[kind]
    if kind in (0x11, 0x12):  # VALUETYPE / CLASS: data = TypeDefinitionIndex
        return M.tdefs[data].full() if data < M.TD_N else "td?%d" % data
    if kind == 0x1D:  # SZARRAY
        return name_type_va(data, depth + 1) + "[]"
    if kind == 0x0F:  # PTR
        return name_type_va(data, depth + 1) + "*"
    if kind == 0x15:  # GENERICINST: data -> Il2CppGenericClass {Il2CppType* type; context{class_inst, method_inst}}
        gtype_va = q(data)
        inst_va = q(data + 8)
        base = name_type_va(gtype_va, depth + 1)
        argc = q(inst_va) & 0xFFFFFFFF
        argv = q(inst_va + 8)
        args = [name_type_va(q(argv + 8 * k), depth + 1) for k in range(argc)]
        return "%s<%s>" % (base, ", ".join(args))
    if kind in (0x13, 0x1E):  # VAR / MVAR
        return "T%d" % data
    return "kind%#x" % kind


def type_name(ti):
    if not (0 <= ti < TYPES_N):
        return "ti?%d" % ti
    try:
        return name_type_va(_type_ptrs[ti])
    except Exception as e:
        return "ti%d(err %s)" % (ti, e)


if __name__ == "__main__":
    import sys
    print("metadata registration at %#x, %d types" % (MR_VA, TYPES_N))
    for a in sys.argv[1:]:
        print(a, type_name(int(a)))


_MR_VALS = None


def field_offsets(td_index):
    """Instance field offsets (bytes, including the 0x10 object header for classes) for a typedef."""
    global _MR_VALS
    if _MR_VALS is None:
        off = raw_of_va(MR_VA)
        _MR_VALS = struct.unpack_from("<16Q", blob, off)
    table = _MR_VALS[11]
    p = q(table + 8 * td_index)
    if not p:
        return []
    t = M.tdefs[td_index]
    o = raw_of_va(p)
    return list(struct.unpack_from("<%di" % t.field_count, blob, o)) if o is not None else []
