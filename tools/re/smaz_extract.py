"""Rebuild SmazSharp's codebook from Trailmakers' own Smaz..cctor (read-only, on-disk).

The cctor fills a 241-bucket encoder table (smaz.c Smaz_cb layout: [len][text][code]...)
from <PrivateImplementationDetails> static array data. Each bucket's bytes come from the
FieldInfo slot it passes to InitializeArray. Writes smaz_codebook.json (code -> text, latin1).
"""
import json, re, struct, sys
import tmcode as C, tmmeta as M, tmtypes as TT

FR_OFF = M.sec(22)[0]
fdv = {}
for i in range(M.FDV_N):
    fi, ti, di = struct.unpack_from('<iii', M.md, M.FDV_OFF + 12 * i)
    fdv[fi] = (ti, di)


def field_blob(slot):
    ti, fidx = struct.unpack_from('<ii', M.md, FR_OFF + 8 * slot)
    data, kind, _ = TT.read_type_at(TT._type_ptrs[ti])
    td = M.tdefs[data]
    gfi = td.field_start + fidx
    name, fti, _tok = M.field(gfi)
    tname = TT.type_name(fti)
    m = re.search(r'Size=(\d+)', tname)
    prim = {"byte": 1, "short": 2, "ushort": 2, "int": 4, "uint": 4, "long": 8, "ulong": 8}
    n = int(m.group(1)) if m else prim.get(tname)
    if n is None or gfi not in fdv:
        raise ValueError("slot %d -> %s %s has no static data" % (slot, name, tname))
    _t, di = fdv[gfi]
    return M.md[M.FDVDATA_OFF + di:M.FDVDATA_OFF + di + n]


def main(out_path):
    (full, mi, va), = C.find('SmazSharp.Smaz', '.cctor')
    lines = C.disasm(va, 0x9000).splitlines()
    start = [k for k, l in enumerate(lines) if l.strip().startswith('5c22e7a')][0]
    buckets = {}
    cur_slot = cur_len = last_edx = None
    for l in lines[start:]:
        m = re.search(r'slot kind4 #(\d+)', l)
        if m:
            cur_slot = int(m.group(1))
        m = re.search(r'mov    edx, (0x[0-9a-f]+|\d+)$', l.strip())
        if m:
            last_edx = int(m.group(1), 0)
        if l.strip().endswith('xor    edx, edx'):
            last_edx = 0
        if '-> 0x70bfc0' in l:  # array allocation: edx = length
            cur_len = last_edx
        if '-> 0x22f0' in l:  # stelem into the bucket table: edx = index
            if last_edx in buckets or len(buckets) == 241:
                break  # the decoder table starts here; the encoder table is complete
            if cur_slot is None:
                raise ValueError("bucket %d without static data near %s" % (last_edx, l))
            buckets[last_edx] = field_blob(cur_slot)[:cur_len]
            cur_slot = None
    rcb = {}
    for k, b in buckets.items():
        p = 0
        while p < len(b):
            ln = b[p]
            if ln == 0:
                break
            rcb[b[p + 1 + ln]] = b[p + 1:p + 1 + ln]
            p += ln + 2
    print("buckets", len(buckets), "codes", len(rcb), "missing", [i for i in range(254) if i not in rcb])
    json.dump({str(k): v.decode('latin1') for k, v in sorted(rcb.items())}, open(out_path, 'w'), indent=0)


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "smaz_codebook.json")
