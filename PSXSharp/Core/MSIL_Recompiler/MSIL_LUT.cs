using PSXSharp.Core.Common;
using System;

namespace PSXSharp.Core.MSIL_Recompiler {
    public static unsafe class MSIL_LUT {

        public static readonly delegate*<CPU_MSIL_Recompiler, Instruction, void>[] MainLookUpTable = new delegate*<CPU_MSIL_Recompiler, Instruction, void>[] {
                &special,   &bxx,       &jump,      &jal,       &beq,        &bne,       &blez,      &bgtz,
                &addi,      &addiu,     &slti,      &sltiu,     &andi,       &ori,       &xori,      &lui,
                &cop0,      &cop1,      &cop2,      &cop3,      &illegal,    &illegal,   &illegal,   &illegal,
                &illegal,   &illegal,   &illegal,   &illegal,   &illegal,    &illegal,   &illegal,   &illegal,
                &lb,        &lh,        &lwl,       &lw,        &lbu,        &lhu,       &lwr,       &illegal,
                &sb,        &sh,        &swl,       &sw,        &illegal,    &illegal,   &swr,       &illegal,
                &lwc0,      &lwc1,      &lwc2,      &lwc3,      &illegal,    &illegal,   &illegal,   &illegal,
                &swc0,      &swc1,      &swc2,      &swc3,      &illegal,    &illegal,   &illegal,   &illegal
        };

        public static readonly delegate*<CPU_MSIL_Recompiler, Instruction, void>[] SpecialLookUpTable = new delegate*<CPU_MSIL_Recompiler, Instruction, void>[] {
                &sll,       &illegal,   &srl,       &sra,       &sllv,      &illegal,   &srlv,      &srav,
                &jr,        &jalr,      &illegal,   &illegal,   &syscall,   &break_,    &illegal,   &illegal,
                &mfhi,      &mthi,      &mflo,      &mtlo,      &illegal,   &illegal,   &illegal,   &illegal,
                &mult,      &multu,     &div,       &divu,      &illegal,   &illegal,   &illegal,   &illegal,
                &add,       &addu,      &sub,       &subu,      &and,       &or,        &xor,       &nor,
                &illegal,   &illegal,   &slt,       &sltu,      &illegal,   &illegal,   &illegal,   &illegal,
                &illegal,   &illegal,   &illegal,   &illegal,   &illegal,   &illegal,   &illegal,   &illegal,
                &illegal,   &illegal,   &illegal,   &illegal,   &illegal,   &illegal,   &illegal,   &illegal
        };

        private static void illegal(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            throw new Exception("Illegal");
        }

        private static void special(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            SpecialLookUpTable[instruction.Sub](cpu, instruction);
        }

        private static void bxx(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint rs = instruction.Rs;
            uint imm = instruction.SignedImm;
            bool bgez = ((instruction.Value >> 16) & 1) == 1;
            bool link = ((instruction.Value >> 17) & 0xF) == 0x8;
            MSIL_JIT.EmitBXX(rs, imm, link, bgez, cpu.CurrentBlock);
        }

        private static void jump(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint target = (cpu.Next_PC & 0xf0000000) | (instruction.JumpImm << 2);
            MSIL_JIT.EmitJump(target, cpu.CurrentBlock);
        }

        private static void jal(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint target = (cpu.Next_PC & 0xf0000000) | (instruction.JumpImm << 2);
            MSIL_JIT.EmitJal(target, cpu.CurrentBlock);
        }

        private static void beq(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            uint imm = instruction.SignedImm;
            MSIL_JIT.EmitBranchIf(rs, rt, imm, BranchIf.BEQ, cpu.CurrentBlock);
        }

        private static void bne(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            uint imm = instruction.SignedImm;
            MSIL_JIT.EmitBranchIf(rs, rt, imm, BranchIf.BNE, cpu.CurrentBlock);
        }

        private static void blez(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint rs = instruction.Rs;
            uint imm = instruction.SignedImm;
            MSIL_JIT.EmitBranchIf(rs, 0, imm, BranchIf.BLEZ, cpu.CurrentBlock);
        }

        private static void bgtz(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint rs = instruction.Rs;
            uint imm = instruction.SignedImm;
            MSIL_JIT.EmitBranchIf(rs, 0, imm, BranchIf.BGTZ, cpu.CurrentBlock);
        }

        private static void addi(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            uint imm = instruction.SignedImm;
            MSIL_JIT.EmitArithmetic_i(rs, rt, imm, ArithmeticSignals.ADD, cpu.CurrentBlock);
        }

        private static void addiu(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            uint imm = instruction.SignedImm;
            MSIL_JIT.EmitArithmeticI_U(rs, rt, imm, ArithmeticSignals.ADD, cpu.CurrentBlock);
        }

        private static void slti(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            uint imm = instruction.SignedImm;
            MSIL_JIT.EmitSlti(rs, rt, imm, true, cpu.CurrentBlock);
        }

        private static void sltiu(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            uint imm = instruction.SignedImm;
            MSIL_JIT.EmitSlti(rs, rt, imm, false, cpu.CurrentBlock);
        }

        private static void andi(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            uint imm = instruction.Imm;
            MSIL_JIT.EmitLogic_i(rs, rt, imm, LogicSignals.AND, cpu.CurrentBlock);
        }

        private static void ori(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            uint imm = instruction.Imm;
            MSIL_JIT.EmitLogic_i(rs, rt, imm, LogicSignals.OR, cpu.CurrentBlock);
        }

        private static void xori(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            uint imm = instruction.Imm;
            MSIL_JIT.EmitLogic_i(rs, rt, imm, LogicSignals.XOR, cpu.CurrentBlock);
        }

        private static void lui(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint rt = instruction.Rt;
            uint imm = instruction.Imm;
            MSIL_JIT.EmitLUI(rt, imm, cpu.CurrentBlock);
        }

        private static void cop0(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            uint rd = instruction.Rd;

            switch (rs) {
                case 0b00100:
                    MSIL_JIT.EmitMTC0(rt, rd, cpu.CurrentBlock);
                    break;

                case 0b00000:
                    MSIL_JIT.EmitMFC0(rt, rd, cpu.CurrentBlock);
                    break;

                case 0b10000:
                    MSIL_JIT.EmitRFE(cpu.CurrentBlock);
                    break;
                default: throw new Exception("Unhandled cop0 instruction: " + instruction.Value.ToString("X"));
            }
        }

        private static void cop1(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            throw new Exception("Illegal");
        }

        private static void cop2(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            if ((instruction.Value >> 25) == 0b0100101) {    //COP2 imm25 command
                MSIL_JIT.EmitCOP2Command(instruction.Value, cpu.CurrentBlock);
                return;
            }

            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            uint rd = instruction.Rd;

            switch (rs) {
                case 0b00000:   //MFC
                    MSIL_JIT.EmitMFC2_CFC2(rt, rd, cpu.CurrentBlock);
                    break;

                case 0b00010:   //CFC                 
                    MSIL_JIT.EmitMFC2_CFC2(rt, rd + 32, cpu.CurrentBlock);
                    break;

                case 0b00110:  //CTC                  
                    MSIL_JIT.EmitMTC2_CTC2(rt, rd + 32, cpu.CurrentBlock);
                    break;

                case 0b00100:  //MTC                  
                    MSIL_JIT.EmitMTC2_CTC2(rt, rd, cpu.CurrentBlock);
                    break;

                default: throw new Exception("Unhandled GTE opcode: " + instruction.Rs.ToString("X"));
            }
        }

        private static void cop3(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            throw new Exception("Illegal");
        }

        private static void lb(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            uint imm = instruction.SignedImm;
            MSIL_JIT.EmitMemoryLoad(rs, rt, imm, MemoryReadWriteSize.BYTE, true, cpu.CurrentBlock);
        }

        private static void lh(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            uint imm = instruction.SignedImm;
            MSIL_JIT.EmitMemoryLoad(rs, rt, imm, MemoryReadWriteSize.HALF, true, cpu.CurrentBlock);
        }

        private static void lw(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            uint imm = instruction.SignedImm;
            MSIL_JIT.EmitMemoryLoad(rs, rt, imm, MemoryReadWriteSize.WORD, false, cpu.CurrentBlock);
        }

        private static void lbu(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            uint imm = instruction.SignedImm;
            MSIL_JIT.EmitMemoryLoad(rs, rt, imm, MemoryReadWriteSize.BYTE, false, cpu.CurrentBlock);
        }

        private static void lhu(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            uint imm = instruction.SignedImm;
            MSIL_JIT.EmitMemoryLoad(rs, rt, imm, MemoryReadWriteSize.HALF, false, cpu.CurrentBlock);
        }

        private static void sb(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            uint imm = instruction.SignedImm;
            MSIL_JIT.EmitMemoryStore(rs, rt, imm, MemoryReadWriteSize.BYTE, cpu.CurrentBlock);
        }

        private static void sh(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            uint imm = instruction.SignedImm;
            MSIL_JIT.EmitMemoryStore(rs, rt, imm, MemoryReadWriteSize.HALF, cpu.CurrentBlock);
        }

        private static void sw(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            uint imm = instruction.SignedImm;
            MSIL_JIT.EmitMemoryStore(rs, rt, imm, MemoryReadWriteSize.WORD, cpu.CurrentBlock);
        }

        private static void swl(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            uint imm = instruction.SignedImm;
            MSIL_JIT.EmitSWL_SWR(rs, rt, imm, true, cpu.CurrentBlock);
        }

        private static void swr(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            uint imm = instruction.SignedImm;
            MSIL_JIT.EmitSWL_SWR(rs, rt, imm, false, cpu.CurrentBlock);
        }

        private static void lwl(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            uint imm = instruction.SignedImm;
            MSIL_JIT.EmitLWL_LWR(rs, rt, imm, true, cpu.CurrentBlock);
        }

        private static void lwr(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            uint imm = instruction.SignedImm;
            MSIL_JIT.EmitLWL_LWR(rs, rt, imm, false, cpu.CurrentBlock);
        }

        private static void lwc0(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            throw new Exception("Illegal");
        }

        private static void lwc1(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            throw new Exception("Illegal");
        }

        private static void lwc2(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            uint imm = instruction.SignedImm;
            MSIL_JIT.EmitLWC2(rs, rt, imm, cpu.CurrentBlock);
        }

        private static void lwc3(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            throw new Exception("Illegal");
        }

        private static void swc0(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            throw new Exception("Illegal");
        }

        private static void swc1(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            throw new Exception("Illegal");
        }

        private static void swc2(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            uint imm = instruction.SignedImm;
            MSIL_JIT.EmitSWC2(rs, rt, imm, cpu.CurrentBlock);
        }

        private static void swc3(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            throw new Exception("Illegal");
        }

        private static void sll(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint rt = instruction.Rt;
            uint rd = instruction.Rd;
            uint sa = instruction.Sa;
            MSIL_JIT.EmitShift(rt, rd, sa, ShiftSignals.LEFT, cpu.CurrentBlock);
        }

        private static void srl(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint rt = instruction.Rt;
            uint rd = instruction.Rd;
            uint sa = instruction.Sa;
            MSIL_JIT.EmitShift(rt, rd, sa, ShiftSignals.RIGHT, cpu.CurrentBlock);
        }

        private static void sra(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint rt = instruction.Rt;
            uint rd = instruction.Rd;
            uint sa = instruction.Sa;
            MSIL_JIT.EmitShift(rt, rd, sa, ShiftSignals.RIGHT_ARITHMETIC, cpu.CurrentBlock);
        }

        private static void sllv(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            uint rd = instruction.Rd;
            MSIL_JIT.EmitShift_v(rs, rt, rd, ShiftSignals.LEFT, cpu.CurrentBlock);
        }

        private static void srlv(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            uint rd = instruction.Rd;
            MSIL_JIT.EmitShift_v(rs, rt, rd, ShiftSignals.RIGHT, cpu.CurrentBlock);
        }

        private static void srav(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            uint rd = instruction.Rd;
            MSIL_JIT.EmitShift_v(rs, rt, rd, ShiftSignals.RIGHT_ARITHMETIC, cpu.CurrentBlock);
        }

        private static void jr(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint rs = instruction.Rs;
            MSIL_JIT.EmitJR(rs, cpu.CurrentBlock);
        }

        private static void jalr(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint rs = instruction.Rs;
            uint rd = instruction.Rd;
            MSIL_JIT.EmitJalr(rs, rd, cpu.CurrentBlock);
        }

        private static void syscall(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            MSIL_JIT.EmitSyscall(cpu.CurrentBlock);
        }

        private static void break_(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            MSIL_JIT.EmitBreak(cpu.CurrentBlock);
        }

        private static void mfhi(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint rd = instruction.Rd;
            MSIL_JIT.EmitMF(rd, true, cpu.CurrentBlock);
        }

        private static void mthi(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint rs = instruction.Rs;
            MSIL_JIT.EmitMT(rs, true, cpu.CurrentBlock);
        }

        private static void mflo(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint rd = instruction.Rd;
            MSIL_JIT.EmitMF(rd, false, cpu.CurrentBlock);
        }

        private static void mtlo(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint rs = instruction.Rs;
            MSIL_JIT.EmitMT(rs, false, cpu.CurrentBlock);
        }

        private static void mult(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            MSIL_JIT.EmitMULT(rs, rt, true, cpu.CurrentBlock);
        }

        private static void multu(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            MSIL_JIT.EmitMULT(rs, rt, false, cpu.CurrentBlock);
        }

        private static void div(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            MSIL_JIT.EmitDIV(rs, rt, true, cpu.CurrentBlock);
        }

        private static void divu(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            MSIL_JIT.EmitDIV(rs, rt, false, cpu.CurrentBlock);
        }

        private static void add(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            uint rd = instruction.Rd;
            MSIL_JIT.EmitArithmetic(rs, rt, rd, ArithmeticSignals.ADD, cpu.CurrentBlock);
        }

        private static void addu(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            uint rd = instruction.Rd;
            MSIL_JIT.EmitArithmeticU(rs, rt, rd, ArithmeticSignals.ADD, cpu.CurrentBlock);
        }

        private static void subu(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            uint rd = instruction.Rd;
            MSIL_JIT.EmitArithmeticU(rs, rt, rd, ArithmeticSignals.SUB, cpu.CurrentBlock);
        }

        private static void sub(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            uint rd = instruction.Rd;
            MSIL_JIT.EmitArithmetic(rs, rt, rd, ArithmeticSignals.SUB, cpu.CurrentBlock);
        }

        public static void or(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            uint rd = instruction.Rd;
            MSIL_JIT.EmitLogic(rs, rt, rd, LogicSignals.OR, cpu.CurrentBlock);
        }

        private static void and(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            uint rd = instruction.Rd;
            MSIL_JIT.EmitLogic(rs, rt, rd, LogicSignals.AND, cpu.CurrentBlock);
        }

        private static void xor(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            uint rd = instruction.Rd;
            MSIL_JIT.EmitLogic(rs, rt, rd, LogicSignals.XOR, cpu.CurrentBlock);
        }

        private static void nor(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            uint rd = instruction.Rd; 
            MSIL_JIT.EmitLogic(rs, rt, rd, LogicSignals.NOR, cpu.CurrentBlock);
        }

        private static void slt(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint rd = instruction.Rd;
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            MSIL_JIT.EmitSlt(rs, rt, rd, true, cpu.CurrentBlock);
        }

        private static void sltu(CPU_MSIL_Recompiler cpu, Instruction instruction) {
            uint rd = instruction.Rd;
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            MSIL_JIT.EmitSlt(rs, rt, rd, false, cpu.CurrentBlock);
        }
    }
}
