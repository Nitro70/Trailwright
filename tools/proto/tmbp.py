"""Prototype Trailmakers blueprint reader (reference for the C# implementation).

PNG -> Unity-order pixels -> RGB of alpha==0 pixels (last pixel excluded) -> header,
structure (LZ4 frame -> protobuf StructureGraphSaveDataProto), identifier, meta.
"""
import struct, sys, math
import numpy as np
from PIL import Image
import lz4.frame


def extract(path):
    a = np.array(Image.open(path).convert("RGBA"))
    h, w, _ = a.shape
    flat = a[::-1].reshape(-1, 4)          # Unity raw order: bottom row first
    last = flat[-1]
    header_len = int(last[3]) | int(last[0]) << 8 | int(last[1]) << 16 | int(last[2]) << 24
    body = flat[:-1]
    sel = body[body[:, 3] == 0][:, :3].reshape(-1).tobytes()
    return header_len, sel, (w, h)


def varint(b, p):
    r = s = 0
    while True:
        x = b[p]; p += 1
        r |= (x & 0x7F) << s; s += 7
        if not x & 0x80:
            return r, p


def fields(b):
    p = 0
    out = []
    while p < len(b):
        key, p = varint(b, p)
        fn, wt = key >> 3, key & 7
        if wt == 0:
            v, p = varint(b, p)
        elif wt == 1:
            v = b[p:p + 8]; p += 8
        elif wt == 2:
            ln, p = varint(b, p); v = b[p:p + ln]; p += ln
        elif wt == 5:
            v = b[p:p + 4]; p += 4
        elif wt == 4:
            out.append((fn, wt, None)); break
        else:
            raise ValueError("wt %d at %d" % (wt, p))
        out.append((fn, wt, v))
    return out, p


def parse_header(h):
    if h[0] == 8:
        f, _ = fields(h[1:])
        d = {fn: v for fn, wt, v in f}
        return dict(version=d.get(1), creator=d.get(2, b"").decode(), structureByteSize=d.get(3, 0),
                    structureIdentifierSize=d.get(4, 0), structureMetaByteSize=d.get(5, 0), format="proto")
    # legacy BinaryFormatter: ClassWithMembersAndTypes; values follow member info
    i = h.index(b"structureMetaByteSize") + len("structureMetaByteSize")
    # member type info: 5 bytes binaryTypeEnums, then additional info for primitives
    # we know the order: uint version, string creator, int, int, int. Values are at the end.
    tail = h[-13:]  # [int structureByteSize][int idSize][int metaSize][0x0b]
    sb, ids, ms = struct.unpack_from("<iii", tail, 0)
    return dict(version=1, creator="", structureByteSize=sb, structureIdentifierSize=ids,
                structureMetaByteSize=ms, format="binaryformatter")


def load(path):
    hl, data, size = extract(path)
    hdr = parse_header(data[:hl])
    off = math.ceil(hl / 3) * 3
    s = hdr["structureByteSize"]
    structure = data[off:off + s]
    ident = data[off + s:off + s + hdr["structureIdentifierSize"]]
    meta = data[off + s + hdr["structureIdentifierSize"]:off + s + hdr["structureIdentifierSize"] + hdr["structureMetaByteSize"]]
    raw = lz4.frame.decompress(structure)
    return hdr, raw, ident, meta, size


if __name__ == "__main__":
    hdr, raw, ident, meta, size = load(sys.argv[1])
    print(hdr, size)
    print("ident", ident.hex(" "))
    print("meta", meta)
    f, _ = fields(raw)
    from collections import Counter
    print(Counter(fn for fn, _, _ in f))


import json as _json, os as _os
_RCB = {int(k): v.encode('latin1') for k, v in _json.load(open(_os.path.join(_os.path.dirname(__file__), 'smaz_codebook.json'))).items()}


def smaz_decode(b):
    out = bytearray(); i = 0
    while i < len(b):
        c = b[i]
        if c == 254:
            out.append(b[i + 1]); i += 2
        elif c == 255:
            n = b[i + 1]; out += b[i + 2:i + 2 + n]; i += 2 + n
        else:
            out += _RCB[c]; i += 1
    return bytes(out)


def parse_meta(meta):
    f, _ = fields(smaz_decode(meta))
    d = {}
    for fn, wt, v in f:
        key = {1: 'Name', 2: 'Description', 3: 'Tags', 4: 'CreatorName', 5: 'CreatorID', 6: 'Platform'}.get(fn, fn)
        val = v.decode('utf-8', 'replace') if isinstance(v, (bytes, bytearray)) else v
        if key == 'Tags':
            d.setdefault(key, []).append(val)
        else:
            d[key] = val
    return d
