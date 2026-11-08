"""A tiny symbolic x86-64 evaluator for leaf getters like GetMinMaxSliderValues(int index).

Supports the handful of instructions these IL2CPP getters use (switch on edx, float constants
from .rdata, instance fields read through `this`). Anything else (calls, pointer chasing into
other objects, unknown opcodes) makes the evaluation give up and return None, so callers can
report "unknown" instead of guessing.
"""
import struct

from capstone import Cs, CS_ARCH_X86, CS_MODE_64
from capstone.x86 import X86_OP_IMM, X86_OP_MEM, X86_OP_REG, X86_REG_RIP

import tmtypes as TT

cs = Cs(CS_ARCH_X86, CS_MODE_64)
cs.detail = True

THIS = ("THIS",)

R64 = {"eax": "rax", "ebx": "rbx", "ecx": "rcx", "edx": "rdx", "esi": "rsi", "edi": "rdi", "r8d": "r8",
       "r9d": "r9", "r10d": "r10", "r11d": "r11", "r12d": "r12", "r13d": "r13", "r14d": "r14", "r15d": "r15",
       "ebp": "rbp", "esp": "rsp"}


class GiveUp(Exception):
    pass


def f2i(f):
    return struct.unpack("<I", struct.pack("<f", f))[0]


def i2f(i):
    return struct.unpack("<f", struct.pack("<I", i & 0xFFFFFFFF))[0]


def run(va, index, field_reader, maxsteps=300):
    """field_reader(offset, size) -> int bits / ('OBJ', name) / None.  Returns dict with rax, xmm0, loads."""
    regs = {"rcx": THIS, "rdx": index, "rsp": 0x100000}
    xmm = {}
    flag = None
    loads = []
    pc = va
    for _ in range(maxsteps):
        off = TT.raw_of_va(pc)
        ins = next(cs.disasm(TT.blob[off:off + 16], pc), None)
        if ins is None:
            raise GiveUp("decode")
        m, ops = ins.mnemonic, ins.operands
        nxt = pc + ins.size

        def reg_name(op):
            n = ins.reg_name(op.reg)
            return R64.get(n, n)

        def read(op, size=4):
            if op.type == X86_OP_IMM:
                return op.imm
            if op.type == X86_OP_REG:
                n = ins.reg_name(op.reg)
                if n.startswith("xmm"):
                    return xmm.get(n, [0, 0, 0, 0])[0]
                v = regs.get(R64.get(n, n))
                if v is None:
                    raise GiveUp("undef reg %s" % n)
                if isinstance(v, int) and (n.startswith("e") or n.endswith("d")):
                    v &= 0xFFFFFFFF
                return v
            if op.type == X86_OP_MEM:
                if op.mem.base == X86_REG_RIP:
                    a = nxt + op.mem.disp
                    o = TT.raw_of_va(a)
                    if o is None:
                        raise GiveUp("bss read")
                    return struct.unpack_from("<I" if size == 4 else "<Q", TT.blob, o)[0]
                base = regs.get(R64.get(ins.reg_name(op.mem.base), ins.reg_name(op.mem.base)))
                if base is THIS and op.mem.index == 0:
                    loads.append(op.mem.disp)
                    v = field_reader(op.mem.disp, size)
                    if v is None:
                        raise GiveUp("field %#x" % op.mem.disp)
                    return v
                raise GiveUp("mem through %r" % (base,))
            raise GiveUp("operand")

        if m in ("push", "pop", "nop") or m in ("sub", "add") and ops[0].type == X86_OP_REG and ins.reg_name(ops[0].reg) == "rsp":
            pass
        elif m == "mov" and ops[0].type == X86_OP_MEM:
            pass  # spills to the stack: ignore
        elif m == "mov":
            dst = reg_name(ops[0])
            v = read(ops[1], 8 if ins.reg_name(ops[0].reg).startswith("r") and not ins.reg_name(ops[0].reg).endswith("d") else 4)
            regs[dst] = v
        elif m in ("test",):
            a, b = read(ops[0]), read(ops[1])
            if not isinstance(a, int) or not isinstance(b, int):
                raise GiveUp("test sym")
            flag = a & b
        elif m in ("cmp",):
            a, b = read(ops[0]), read(ops[1])
            if not isinstance(a, int) or not isinstance(b, int):
                raise GiveUp("cmp sym")
            flag = (a - b) & 0xFFFFFFFF
        elif m in ("sub", "add", "dec", "inc"):
            dst = reg_name(ops[0])
            a = read(ops[0])
            b = read(ops[1]) if len(ops) > 1 else 1
            if not isinstance(a, int):
                raise GiveUp("arith sym")
            r = (a - b if m in ("sub", "dec") else a + b) & 0xFFFFFFFF
            regs[dst] = r
            flag = r
        elif m in ("je", "jne", "jmp", "jz", "jnz"):
            tgt = ops[0].imm
            if m == "jmp" or (m in ("je", "jz") and flag == 0) or (m in ("jne", "jnz") and flag != 0):
                nxt = tgt
        elif m in ("movss", "movd") and ops[0].type == X86_OP_REG and ins.reg_name(ops[0].reg).startswith("xmm"):
            v = read(ops[1])
            if isinstance(v, tuple):
                raise GiveUp("float from object")
            xmm[ins.reg_name(ops[0].reg)] = [v & 0xFFFFFFFF, 0, 0, 0]
        elif m in ("movss", "movd") and ops[0].type == X86_OP_REG:
            regs[reg_name(ops[0])] = xmm.get(ins.reg_name(ops[1].reg), [0])[0]
        elif m == "xorps":
            a, b = ins.reg_name(ops[0].reg), ins.reg_name(ops[1].reg)
            if a != b:
                raise GiveUp("xorps")
            xmm[a] = [0, 0, 0, 0]
        elif m in ("movaps", "movups") and ops[1].type == X86_OP_REG:
            xmm[ins.reg_name(ops[0].reg)] = list(xmm.get(ins.reg_name(ops[1].reg), [0, 0, 0, 0]))
        elif m == "unpcklps":
            a = xmm.get(ins.reg_name(ops[0].reg), [0, 0, 0, 0])
            b = xmm.get(ins.reg_name(ops[1].reg), [0, 0, 0, 0])
            xmm[ins.reg_name(ops[0].reg)] = [a[0], b[0], a[1], b[1]]
        elif m == "movq" and ops[0].type == X86_OP_REG and not ins.reg_name(ops[0].reg).startswith("xmm"):
            x = xmm.get(ins.reg_name(ops[1].reg), [0, 0, 0, 0])
            regs[reg_name(ops[0])] = x[0] | (x[1] << 32)
        elif m in ("mulss", "divss", "addss", "subss"):
            a = i2f(xmm.get(ins.reg_name(ops[0].reg), [0])[0])
            bv = read(ops[1])
            b = i2f(bv) if isinstance(bv, int) else None
            if b is None:
                raise GiveUp("arith float sym")
            r = {"mulss": a * b, "divss": a / b if b else float("inf"), "addss": a + b, "subss": a - b}[m]
            xmm[ins.reg_name(ops[0].reg)][0] = f2i(r)
        elif m == "cvtsi2ss" or m == "cvtdq2ps":
            v = read(ops[1])
            xmm[ins.reg_name(ops[0].reg)] = [f2i(float(v if v < 2 ** 31 else v - 2 ** 32)), 0, 0, 0]
        elif m == "ret":
            return {"rax": regs.get("rax"), "xmm0": xmm.get("xmm0", [None])[0], "loads": loads}
        elif m == "xor" and ops[0].type == X86_OP_REG and ops[1].type == X86_OP_REG and ops[0].reg == ops[1].reg:
            regs[reg_name(ops[0])] = 0
        else:
            raise GiveUp("insn %s %s" % (m, ins.op_str))
        pc = nxt
    raise GiveUp("too many steps")
