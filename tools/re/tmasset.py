"""Parse type-tree-stripped Unity MonoBehaviour data using the IL2CPP metadata (read-only).

Unity serialization rules applied (Unity 2022/6, player build):
  * serialized fields: instance, not literal/readonly/[NonSerialized], public or [SerializeField]
  * base-class fields first (stopping at MonoBehaviour / ScriptableObject / Object)
  * 1- and 2-byte primitives are padded to 4 (each top-level-or-nested field is aligned)
  * string / array / List<T>: int32 count + payload, then align to 4
  * UnityEngine.Object references are PPtr (int32 fileID + int64 pathID)
  * [Serializable] classes and structs are inlined; known Unity structs have fixed layouts
"""
import struct

import tmmeta as M
import tmtypes as TT

FA_STATIC, FA_INITONLY, FA_LITERAL, FA_NOTSERIALIZED = 0x10, 0x20, 0x40, 0x80
TA_SERIALIZABLE = 0x2000

UNITY_STRUCTS = {
    "UnityEngine.Vector2": "ff", "UnityEngine.Vector3": "fff", "UnityEngine.Vector4": "ffff",
    "UnityEngine.Quaternion": "ffff", "UnityEngine.Color": "ffff", "UnityEngine.Rect": "ffff",
    "UnityEngine.Bounds": "ffffff", "UnityEngine.Matrix4x4": "f" * 16, "UnityEngine.Color32": "BBBB",
    "UnityEngine.Vector2Int": "ii", "UnityEngine.Vector3Int": "iii", "UnityEngine.LayerMask": "I",
    "UnityEngine.BoundsInt": "iiiiii", "UnityEngine.RectInt": "iiii", "UnityEngine.Hash128": "IIII",
}
PRIM = {2: ("<?", 1), 3: ("<H", 2), 4: ("<b", 1), 5: ("<B", 1), 6: ("<h", 2), 7: ("<H", 2), 8: ("<i", 4),
        9: ("<I", 4), 0xA: ("<q", 8), 0xB: ("<Q", 8), 0xC: ("<f", 4), 0xD: ("<d", 8)}

_unity_object_td = {}


def _td_by_name(full):
    for t in M.tdefs:
        if t.full() == full:
            return t
    return None


UOBJ = _td_by_name("UnityEngine.Object")
STOP = {"UnityEngine.MonoBehaviour", "UnityEngine.ScriptableObject", "UnityEngine.Object",
        "UnityEngine.Behaviour", "UnityEngine.Component", "System.Object", "System.ValueType"}


def td_of_type_va(va):
    data, kind, bits = TT.read_type_at(va)
    if kind in (0x11, 0x12):
        return M.tdefs[data]
    if kind == 0x15:  # generic instance: base typedef
        g = TT.q(data)
        d2, k2, _ = TT.read_type_at(g)
        return M.tdefs[d2]
    return None


def parent_td(t):
    if t.parent < 0:
        return None
    return td_of_type_va(TT._type_ptrs[t.parent])


def is_unity_object(t):
    seen = 0
    while t is not None and seen < 40:
        if t.full() == "UnityEngine.Object":
            return True
        t = parent_td(t)
        seen += 1
    return False


def has_attr(img, token, name):
    return any(a[0] == name for a in M.attributes(img, token))


GP_OFF = M.sec(12)[0]


def gparam_num(gpi):
    # Il2CppGenericParameter (14 bytes): owner 2, name 4, constraintsStart 2, constraintsCount 2, num 2, flags 2
    return struct.unpack_from("<H", M.md, GP_OFF + 14 * gpi + 10)[0]


def resolve(va, ctx):
    data, kind, bits = TT.read_type_at(va)
    if kind == 0x13 and ctx:
        return ctx[gparam_num(data)]
    return va


def generic_args(va, ctx):
    data, kind, bits = TT.read_type_at(va)
    if kind != 0x15:
        return []
    inst = TT.q(data + 8)
    argc = TT.q(inst) & 0xFFFFFFFF
    argv = TT.q(inst + 8)
    return [resolve(TT.q(argv + 8 * k), ctx) for k in range(argc)]


def serialized_fields(t, ctx=None):
    """[(name, field type VA, special, ctx)] base class first, generic args substituted."""
    chain = []
    cur, cur_ctx = t, ctx or []
    while cur is not None and cur.full() not in STOP:
        chain.append((cur, cur_ctx))
        if cur.parent < 0:
            break
        pva = resolve(TT._type_ptrs[cur.parent], cur_ctx)
        nxt = td_of_type_va(pva)
        cur_ctx = generic_args(pva, cur_ctx)
        cur = nxt
    out = []
    for c, cctx in reversed(chain):
        for k in range(c.field_count):
            name, ti, tok = M.field(c.field_start + k)
            va = TT._type_ptrs[ti]
            data, kind, bits = TT.read_type_at(va)
            attrs = bits & 0xFFFF
            if attrs & (FA_STATIC | FA_LITERAL | FA_NOTSERIALIZED | FA_INITONLY):
                continue
            public = (attrs & 7) == 6
            if not public and not has_attr(c.image, tok, "SerializeField"):
                continue
            if has_attr(c.image, tok, "SerializeReference"):
                out.append((name, va, "ref", cctx))
                continue
            out.append((name, va, None, cctx))
    return out


class Reader:
    def __init__(self, raw, p=0):
        self.b, self.p = raw, p

    def align(self):
        self.p = (self.p + 3) & ~3

    def unpack(self, fmt, n):
        v = struct.unpack_from(fmt, self.b, self.p)[0]
        self.p += n
        return v


def enum_underlying(t):
    if t.parent >= 0:
        _d, kind, _b = TT.read_type_at(TT._type_ptrs[t.parent])
        if kind in PRIM:
            return kind
    for k in range(t.field_count):
        name, ti, tok = M.field(t.field_start + k)
        if name == "value__":
            _d, kind, _b = TT.read_type_at(TT._type_ptrs[ti])
            return kind
    return 8


def is_enum(t):
    # Il2CppTypeDefinition.bitfield: bit0 valuetype, bit1 enumtype. In metadata v39 an enum's
    # parentIndex holds its underlying type (int, byte...) rather than System.Enum.
    return bool((t.bitfield >> 1) & 1)


def serializable_type(va, ctx=None):
    """Return False if Unity would skip a field of this type."""
    va = resolve(va, ctx)
    data, kind, bits = TT.read_type_at(va)
    if kind in PRIM or kind == 0xE:
        return True
    if kind == 0x1D:
        el = data
        d2, k2, _ = TT.read_type_at(el)
        if k2 in (0x1D,):
            return False
        return serializable_type(el, ctx)
    if kind == 0x15:
        g = TT.q(data)
        name = TT.name_type_va(g)
        if name == "System.Collections.Generic.List`1":
            inst = TT.q(data + 8)
            argv = TT.q(inst + 8)
            return serializable_type(TT.q(argv), ctx)
        t = td_of_type_va(va)
        return t is not None and (is_unity_object(t) or bool(t.flags & TA_SERIALIZABLE))
    if kind in (0x11, 0x12):
        t = M.tdefs[data]
        full = t.full()
        if full in UNITY_STRUCTS or is_enum(t) or is_unity_object(t):
            return True
        if full in ("UnityEngine.AnimationCurve", "UnityEngine.Gradient"):
            return True
        if full.startswith("System.") or full.startswith("UnityEngine.") and not (t.flags & TA_SERIALIZABLE):
            return False
        return bool(t.flags & TA_SERIALIZABLE)
    return False


def read_value(r, va, depth=0, ctx=None):
    va = resolve(va, ctx)
    data, kind, bits = TT.read_type_at(va)
    if kind in PRIM:
        fmt, n = PRIM[kind]
        v = r.unpack(fmt, n)
        if n < 4:
            r.align()
        return v
    if kind == 0xE:
        n = r.unpack("<i", 4)
        s = r.b[r.p:r.p + n].decode("utf-8", "replace")
        r.p += n
        r.align()
        return s
    if kind == 0x1D or kind == 0x15 and TT.name_type_va(TT.q(data)) == "System.Collections.Generic.List`1":
        if kind == 0x1D:
            el = data
        else:
            inst = TT.q(data + 8)
            el = TT.q(TT.q(inst + 8))
        n = r.unpack("<i", 4)
        if n < 0 or n > 1_000_000:
            raise ValueError("bad array length %d at %d" % (n, r.p - 4))
        d2, k2, _ = TT.read_type_at(el)
        if k2 in (5, 4):  # byte[] stays raw
            v = bytes(r.b[r.p:r.p + n])
            r.p += n
            r.align()
            return v
        el = resolve(el, ctx)
        d2, k2, _ = TT.read_type_at(el)
        if k2 in (5, 4) and False:
            pass
        out = [read_value(r, el, depth + 1, ctx) for _ in range(n)]
        r.align()
        return out
    t = td_of_type_va(va)
    if t is None:
        raise ValueError("unhandled type kind %#x" % kind)
    full = t.full()
    if full in UNITY_STRUCTS:
        vals = [r.unpack("<" + c, 4 if c != "B" else 1) for c in UNITY_STRUCTS[full]]
        if full == "UnityEngine.Color32":
            r.align()
        return vals
    if is_unity_object(t):
        return ("PPtr", r.unpack("<i", 4), r.unpack("<q", 8))
    if is_enum(t):
        k = enum_underlying(t)
        fmt, n = PRIM.get(k, ("<i", 4))
        v = r.unpack(fmt, n)
        if n < 4:
            r.align()
        return v
    if full == "UnityEngine.AnimationCurve":
        keys = r.unpack("<i", 4)
        r.p += keys * 28  # time, value, inSlope, outSlope, weightedMode, inWeight, outWeight
        r.p += 12  # preInfinity, postInfinity, rotationOrder
        return ("AnimationCurve", keys)
    if full == "UnityEngine.Gradient":
        r.p += 8 * 16 + 8 * 2 * 2 + 4  # 8 Color keys(16) + 16 ushort times + mode/count bytes
        return ("Gradient",)
    if depth > 10:
        return None
    return read_object(r, t, depth + 1, generic_args(va, ctx))


def read_object(r, t, depth=0, ctx=None):
    obj = {"__type": t.full()}
    for name, va, special, fctx in serialized_fields(t, ctx):
        if special == "ref":
            obj[name] = ("SerializeReference", r.unpack("<q", 8))
            continue
        if not serializable_type(va, fctx):
            continue
        obj[name] = read_value(r, va, depth, fctx)
    return obj


def read_monobehaviour(raw, class_full):
    t = _td_by_name(class_full)
    r = Reader(raw)
    hdr = {"m_GameObject": (r.unpack("<i", 4), r.unpack("<q", 8)), "m_Enabled": r.unpack("<B", 1)}
    r.align()
    hdr["m_Script"] = (r.unpack("<i", 4), r.unpack("<q", 8))
    n = r.unpack("<i", 4)
    hdr["m_Name"] = r.b[r.p:r.p + n].decode("utf-8", "replace")
    r.p += n
    r.align()
    obj = read_object(r, t)
    obj.update(hdr)
    obj["__consumed"] = r.p
    obj["__size"] = len(raw)
    return obj
