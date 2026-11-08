"""Find and disassemble Trailmakers methods by class + name (read-only, on-disk).

Method addresses come from each assembly's Il2CppCodeGenModule:
{ char* moduleName; uint32 methodPointerCount; void** methodPointers; ... }, found by the
module name string. methodPointers[(token & 0xFFFFFF) - 1] is the method's code.
Call targets are annotated with method names; RIP-relative metadata slots with what they hold.

  python tmcode.py <Class> <Method> [maxbytes]
"""
import struct
import sys

from capstone import Cs, CS_ARCH_X86, CS_MODE_64
from capstone.x86 import X86_OP_MEM, X86_REG_RIP, X86_OP_IMM

import tmmeta as M
import tmtypes as TT

BASE, blob, SECS = TT.BASE, TT.blob, TT.SECS
_rdata = [s for s in SECS if s[0] == ".rdata"][0]
_data_secs = [s for s in SECS if s[0] in (".data", ".rdata")]


def _va_of_raw(off):
    for _n, sva, vs, r, rs in SECS:
        if r <= off < r + rs:
            return BASE + sva + (off - r)
    return None


def _find_qword(value):
    needle = struct.pack("<Q", value)
    out = []
    for _n, sva, vs, r, rs in _data_secs:
        data = blob[r:r + rs]
        i = data.find(needle)
        while i != -1:
            if i % 8 == 0:
                out.append(BASE + sva + i)
            i = data.find(needle, i + 1)
    return out


_module_cache = {}


def module_pointers(image_name):
    if image_name in _module_cache:
        return _module_cache[image_name]
    s = image_name.encode() + b"\0"
    _n, sva, vs, r, rs = _rdata
    data = blob[r:r + rs]
    i = data.find(b"\0" + s)
    ptrs = None
    while i != -1 and ptrs is None:
        str_va = BASE + sva + i + 1
        for ref in _find_qword(str_va):
            count = TT.q(ref + 8) & 0xFFFFFFFF
            mp = TT.q(ref + 16)
            if 0 < count < 200000 and mp and BASE < mp < BASE + 0x20000000:
                ptrs = [TT.q(mp + 8 * k) for k in range(count)]
                break
        i = data.find(b"\0" + s, i + 1)
    _module_cache[image_name] = ptrs
    return ptrs


def method_va(mi):
    name, decl, tok, _pc = M.method(mi)
    img = M.tdefs[decl].image
    ptrs = module_pointers(M.images[img].name)
    rid = tok & 0xFFFFFF
    if not ptrs or rid - 1 >= len(ptrs):
        return None
    return ptrs[rid - 1]


_names = None


def va_names():
    """VA -> 'Class.Method' for every method in the images we have looked at plus core ones."""
    global _names
    if _names is None:
        _names = {}
    return _names


def index_image(img):
    names = va_names()
    im = M.images[img]
    for k in range(im.type_count):
        t = M.tdefs[im.type_start + k]
        if t.method_start < 0:
            continue
        for j in range(t.method_count):
            mi = t.method_start + j
            va = method_va(mi)
            if va:
                names.setdefault(va, "%s.%s" % (t.full(), M.method(mi)[0]))


def find(cls, meth):
    out = []
    for t in M.tdefs:
        if t.full() == cls or t.name == cls:
            for j in range(t.method_count):
                mi = t.method_start + j
                if M.method(mi)[0] == meth:
                    out.append((t.full(), mi, method_va(mi)))
    return out


def decode_slot(va):
    """IL2CPP metadata usage slot: encoded (kind<<29 | idx<<1 | 1) before runtime init."""
    v = TT.q(va)
    if v is None:
        return None
    if v & 1 and v >> 32 == 0:
        kind = (v >> 29) & 7
        idx = (v & 0x1FFFFFFF) >> 1
        if kind == 1:
            return "TypeInfo %s" % TT.type_name(idx)
        if kind == 2:
            return "Il2CppType %s" % TT.type_name(idx)
        if kind == 3:
            n, d, _t, _p = M.method(idx)
            return "MethodDef %s.%s" % (M.tdefs[d].full(), n)
        if kind == 5:
            s = M.sec(0)
            off, = struct.unpack_from("<i", M.md, s[0] + 4 * idx)
            nxt, = struct.unpack_from("<i", M.md, s[0] + 4 * (idx + 1))
            d0 = M.sec(1)[0]
            return 'String "%s"' % M.md[d0 + off:d0 + nxt].decode(errors="replace")
        return "slot kind%d #%d" % (kind, idx)
    return None


cs = Cs(CS_ARCH_X86, CS_MODE_64)
cs.detail = True


def disasm(va, maxlen=0x1000):
    off = TT.raw_of_va(va)
    names = va_names()
    lines = []
    last_end = va
    for ins in cs.disasm(blob[off:off + maxlen], va):
        note = ""
        for op in ins.operands:
            if op.type == X86_OP_MEM and op.mem.base == X86_REG_RIP:
                tgt = ins.address + ins.size + op.mem.disp
                d = decode_slot(tgt)
                note = "   ; [%#x] %s" % (tgt - BASE, d or "")
        if ins.mnemonic in ("call", "jmp") and ins.operands and ins.operands[0].type == X86_OP_IMM:
            tgt = ins.operands[0].imm
            note = "   ; -> %s" % names.get(tgt, "%#x" % (tgt - BASE))
        lines.append("  %x: %-6s %s%s" % (ins.address - BASE, ins.mnemonic, ins.op_str, note))
        if ins.mnemonic.startswith("j") and ins.operands and ins.operands[0].type == X86_OP_IMM:
            last_end = max(last_end, ins.operands[0].imm)
        if ins.mnemonic in ("ret", "int3") and ins.address >= last_end:
            break
    return "\n".join(lines)


if __name__ == "__main__":
    cls, meth = sys.argv[1], sys.argv[2]
    maxlen = int(sys.argv[3], 0) if len(sys.argv) > 3 else 0x1000
    for img_name in sys.argv[4:]:
        for i, im in enumerate(M.images):
            if im.name == img_name:
                index_image(i)
    hits = find(cls, meth)
    for full, mi, va in hits:
        t = M.tdefs[M.method(mi)[1]]
        index_image(t.image)
        for core in ("mscorlib.dll", "protobuf-net.dll", "UnityEngine.CoreModule.dll", "Flashbulb.Core.dll"):
            for i, im in enumerate(M.images):
                if im.name == core:
                    index_image(i)
        print("=== %s.%s  mi=%d va=%s" % (full, meth, mi, hex(va - BASE) if va else None))
        if va:
            print(disasm(va, maxlen))
