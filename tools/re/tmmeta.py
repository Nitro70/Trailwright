"""Read-only reader for Trailmakers' IL2CPP global-metadata.dat (metadata version 39).

Il2CppDumper 6.7.46 refuses v39, so this decodes just what the blueprint work needs:
type definitions, fields, methods, images and custom attribute blobs (to recover
protobuf-net [ProtoContract]/[ProtoMember(n)] tags and names).

Layouts were worked out from the file itself (entry size = section size / count):
  typeDefinition 82 bytes, field 12, method 32, image 36, attributeDataRange 8.
TypeDefinitionIndex and GenericContainerIndex are 2 bytes wide in this build
(fewer than 65536 entries), TypeIndex stays 4 bytes.

Usage:
  python tmmeta.py types <substring>        list matching types with fields + attributes
  python tmmeta.py ns <namespace-prefix>    same, for a whole namespace
  python tmmeta.py proto                     every [ProtoContract] type in every image
"""
import struct
import sys

import os as _os
GAME = _os.environ.get("TRAILMAKERS_DIR", "C:/Program Files (x86)/Steam/steamapps/common/Trailmakers")
META = GAME + "/Trailmakers_Data/il2cpp_data/Metadata/global-metadata.dat"

md = open(META, "rb").read()
_sanity, VERSION = struct.unpack_from("<Ii", md, 0)
_h = struct.unpack_from("<93i", md, 8)


def sec(i):
    return _h[i * 3:i * 3 + 3]


STR_OFF = sec(2)[0]
METH_OFF, _, METH_N = sec(5)
FIELD_OFF, _, FIELD_N = sec(11)
NESTED_OFF, _, NESTED_N = sec(15)
TD_OFF, _, TD_N = sec(19)
IMG_OFF, _, IMG_N = sec(20)
ATTR_OFF, ATTR_SIZE, _ = sec(24)
AR_OFF, _, AR_N = sec(25)
FDV_OFF, _, FDV_N = sec(7)
FDVDATA_OFF = sec(8)[0]

assert VERSION == 39, VERSION


def ident(idx):
    if idx < 0:
        return ""
    end = md.index(b"\0", STR_OFF + idx)
    return md[STR_OFF + idx:end].decode(errors="replace")


class TypeDef:
    __slots__ = ("index", "name", "ns", "byval", "declaring", "parent", "gc", "flags", "field_start",
                 "method_start", "event_start", "property_start", "nested_start", "interfaces_start",
                 "vtable_start", "ioff_start", "method_count", "property_count", "field_count",
                 "event_count", "nested_count", "vtable_count", "interfaces_count", "ioff_count",
                 "bitfield", "token", "image", "outer")

    def full(self):
        if self.outer is not None:
            return tdefs[self.outer].full() + "/" + self.name
        return (self.ns + "." if self.ns else "") + self.name


tdefs = []
for i in range(TD_N):
    o = TD_OFF + i * 82
    t = TypeDef()
    t.index = i
    (n, ns, t.byval, t.declaring, t.parent) = struct.unpack_from("<iiiii", md, o)
    t.name, t.ns = ident(n), ident(ns)
    t.gc, = struct.unpack_from("<h", md, o + 20)
    (t.flags, t.field_start, t.method_start, t.event_start, t.property_start, t.nested_start,
     t.interfaces_start, t.vtable_start, t.ioff_start) = struct.unpack_from("<Iiiiiiiii", md, o + 22)
    (t.method_count, t.property_count, t.field_count, t.event_count, t.nested_count,
     t.vtable_count, t.interfaces_count, t.ioff_count) = struct.unpack_from("<8H", md, o + 58)
    t.bitfield, t.token = struct.unpack_from("<II", md, o + 74)
    t.image = None
    t.outer = None
    tdefs.append(t)

nested = struct.unpack_from("<%di" % NESTED_N, md, NESTED_OFF)
for t in tdefs:
    if t.nested_count and t.nested_start >= 0:
        for k in range(t.nested_count):
            tdefs[nested[t.nested_start + k]].outer = t.index


class Image:
    pass


images = []
for i in range(IMG_N):
    o = IMG_OFF + i * 36
    im = Image()
    n, im.asm = struct.unpack_from("<ii", md, o)
    im.name = ident(n)
    im.type_start, im.type_count = struct.unpack_from("<HH", md, o + 8)
    (im.exp_start, im.exp_count, im.entry, im.token, im.ca_start, im.ca_count) = struct.unpack_from("<iiiIii", md, o + 12)
    images.append(im)
    for k in range(im.type_count):
        tdefs[im.type_start + k].image = i


def field(i):
    n, ti, tok = struct.unpack_from("<iiI", md, FIELD_OFF + i * 12)
    return ident(n), ti, tok


def method(i):
    # nameIndex 4, declaringType 2, returnType 4, returnParameterToken 4, parameterStart 4,
    # genericContainerIndex 2, token 4, flags 2, iflags 2, slot 2, parameterCount 2 = 32
    o = METH_OFF + i * 32
    n, decl, ret, rpt, pstart, gc, tok = struct.unpack_from("<iHiIihI", md, o)
    flags, iflags, slot, pcount = struct.unpack_from("<HHHH", md, o + 24)
    return ident(n), decl, tok, pcount


# ---- custom attributes -------------------------------------------------------

class R:
    def __init__(self, off):
        self.p = off

    def u8(self):
        v = md[self.p]
        self.p += 1
        return v

    def cu32(self):
        v = self.u8()
        if v & 0x80 == 0:
            return v
        if v & 0xC0 == 0x80:
            return ((v & 0x3F) << 8) | self.u8()
        if v & 0xE0 == 0xC0:
            b1, b2, b3 = self.u8(), self.u8(), self.u8()
            return ((v & 0x1F) << 24) | (b1 << 16) | (b2 << 8) | b3
        if v == 0xF0:
            x, = struct.unpack_from("<I", md, self.p)
            self.p += 4
            return x
        if v == 0xFE:
            return 0xFFFFFFFE
        if v == 0xFF:
            return 0xFFFFFFFF
        raise ValueError("bad compressed int %#x at %d" % (v, self.p - 1))

    def ci32(self):
        e = self.cu32()
        if e == 0xFFFFFFFF:
            return -2 ** 31
        neg = e & 1
        e >>= 1
        return -(e + 1) if neg else e

    def fmt(self, f, n):
        v = struct.unpack_from(f, md, self.p)[0]
        self.p += n
        return v


def read_type_enum(r):
    t = r.u8()
    enum_ti = None
    if t == 0x55:
        enum_ti = r.ci32()
    return t, enum_ti


def read_value(r, t, enum_ti=None):
    if t == 0x55:  # enum: assume int32 underlying (protobuf-net enums are)
        return ("enum", enum_ti, r.ci32())
    if t == 0x02:
        return bool(r.u8())
    if t == 0x03:
        return chr(r.fmt("<H", 2))
    if t in (0x04, 0x05):
        return r.u8()
    if t in (0x06, 0x07):
        return r.fmt("<h", 2)
    if t == 0x08:
        return r.ci32()
    if t == 0x09:
        return r.cu32()
    if t in (0x0A, 0x0B):
        return r.fmt("<q", 8)
    if t == 0x0C:
        return r.fmt("<f", 4)
    if t == 0x0D:
        return r.fmt("<d", 8)
    if t == 0x0E:
        ln = r.ci32()
        if ln == -1:
            return None
        s = md[r.p:r.p + ln].decode(errors="replace")
        r.p += ln
        return s
    if t == 0x1D:
        ln = r.ci32()
        if ln == -1:
            return None
        et, eti = read_type_enum(r)
        different = r.u8()
        out = []
        for _ in range(ln):
            tt, tti = (read_type_enum(r) if different else (et, eti))
            out.append(read_value(r, tt, tti))
        return out
    if t == 0xFF:  # System.Type -> Il2CppType index
        return ("type", r.ci32())
    if t == 0x1C:  # object: boxed, actual type follows
        tt, tti = read_type_enum(r)
        return read_value(r, tt, tti)
    raise ValueError("unhandled attribute value type %#x" % t)


ranges = [struct.unpack_from("<Ii", md, AR_OFF + 8 * i) for i in range(AR_N)]


def attr_index_for_image(img):
    im = images[img]
    m = {}
    for k in range(im.ca_count):
        idx = im.ca_start + k
        tok, start = ranges[idx]
        end = ranges[idx + 1][1] if idx + 1 < AR_N else ATTR_SIZE
        m[tok] = (start, end)
    return m


_attr_cache = {}


def attributes(img, token):
    if img not in _attr_cache:
        _attr_cache[img] = attr_index_for_image(img)
    hit = _attr_cache[img].get(token)
    if not hit:
        return []
    start, end = hit
    r = R(ATTR_OFF + start)
    count = r.cu32()
    ctors = [r.fmt("<i", 4) for _ in range(count)]
    out = []
    for c in ctors:
        mname, decl, _tok, _pc = method(c)
        aname = tdefs[decl].name if 0 <= decl < TD_N else "?"
        try:
            argc, fieldc, propc = r.cu32(), r.cu32(), r.cu32()
            args = [read_value(r, *read_type_enum(r)) for _ in range(argc)]
            named = {}
            for _ in range(fieldc + propc):
                v = read_value(r, *read_type_enum(r))
                mi = r.ci32()
                owner = tdefs[decl]
                if mi < 0:
                    mi = -(mi + 1)
                    owner = tdefs[r.cu32()]
                named["#%d@%s" % (mi, owner.name)] = v
            out.append((aname, args, named))
        except Exception as e:  # keep going; report what broke
            out.append((aname, "DECODE-ERROR %s" % e, {}))
            break
    return out


def props_of(t):
    """property name -> index list (for naming named args)."""
    return []


def _tn(ti):
    try:
        import tmtypes
        return tmtypes.type_name(ti)
    except Exception as e:  # tmtypes needs pefile and GameAssembly.dll
        return "ti=%d" % ti


def describe(t, show_all_fields=True):
    lines = []
    img = t.image
    ta = attributes(img, t.token)
    for a in ta:
        lines.append("  [%s %s %s]" % a)
    lines.insert(0, "%s  (td %d, image %s, fields %d, flags %#x)" % (
        t.full(), t.index, images[img].name if img is not None else "?", t.field_count, t.flags))
    for k in range(t.field_count):
        name, ti, tok = field(t.field_start + k)
        fa = attributes(img, tok)
        if not show_all_fields and not fa:
            continue
        tag = ""
        for a in fa:
            tag += " [%s %s %s]" % a
        lines.append("    %-34s %-45s%s" % (name, _tn(ti), tag))
    return "\n".join(lines)


def main(argv):
    cmd = argv[1] if len(argv) > 1 else "proto"
    arg = argv[2] if len(argv) > 2 else ""
    if cmd == "types":
        for t in tdefs:
            if arg.lower() in t.full().lower():
                print(describe(t))
    elif cmd == "exact":
        for t in tdefs:
            if t.full() == arg or t.name == arg:
                print(describe(t))
    elif cmd == "ns":
        for t in tdefs:
            if t.ns.startswith(arg):
                print(describe(t))
    elif cmd == "proto":
        for t in tdefs:
            if t.image is None:
                continue
            ta = attributes(t.image, t.token)
            if any(a[0] == "ProtoContractAttribute" for a in ta):
                print(describe(t))
    elif cmd == "image":
        for i, im in enumerate(images):
            if arg.lower() in im.name.lower():
                print(i, im.name, "types", im.type_start, im.type_count)
                for k in range(im.type_count):
                    print("   ", tdefs[im.type_start + k].full())


if __name__ == "__main__":
    main(sys.argv)


_fdv_index = None


def enum_values(t):
    """{name: value} for an enum typedef (metadata v29+ default-value blobs: compressed ints)."""
    global _fdv_index
    if _fdv_index is None:
        _fdv_index = {}
        for i in range(FDV_N):
            fi, ti, di = struct.unpack_from("<iii", md, FDV_OFF + 12 * i)
            _fdv_index[fi] = di
    import tmtypes as TT
    out = {}
    kind = 8
    if t.parent >= 0:
        _d, kind, _b = TT.read_type_at(TT._type_ptrs[t.parent])
    for k in range(t.field_count):
        gfi = t.field_start + k
        name, ti, tok = field(gfi)
        if name == "value__" or gfi not in _fdv_index:
            continue
        r = R(FDVDATA_OFF + _fdv_index[gfi])
        if kind in (4, 5):
            v = md[r.p] if kind == 5 else struct.unpack_from("<b", md, r.p)[0]
        elif kind in (6, 7):
            v = struct.unpack_from("<h" if kind == 6 else "<H", md, r.p)[0]
        elif kind == 9:
            v = r.cu32()
        elif kind in (0xA, 0xB):
            v = struct.unpack_from("<q", md, r.p)[0]
        else:
            v = r.ci32()
        out[name] = v
    return out
