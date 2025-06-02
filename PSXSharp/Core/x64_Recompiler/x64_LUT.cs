using Iced.Intel;
using PSXSharp.Core.Common;
using System;
using Instruction = PSXSharp.Core.Common.Instruction;

namespace PSXSharp.Core.x64_Recompiler {
    public static unsafe class x64_LUT {

        public static readonly delegate*<Instruction, Assembler, void>[] MainLookUpTable = [
                &special,   &bxx,       &jump,      &jal,       &beq,        &bne,       &blez,      &bgtz,
                &addi,      &addiu,     &slti,      &sltiu,     &andi,       &ori,       &xori,      &lui,
                &cop0,      &cop1,      &cop2,      &cop3,      &illegal,    &illegal,   &illegal,   &illegal,
                &illegal,   &illegal,   &illegal,   &illegal,   &illegal,    &illegal,   &illegal,   &illegal,
                &lb,        &lh,        &lwl,       &lw,        &lbu,        &lhu,       &lwr,       &illegal,
                &sb,        &sh,        &swl,       &sw,        &illegal,    &illegal,   &swr,       &illegal,
                &lwc0,      &lwc1,      &lwc2,      &lwc3,      &illegal,    &illegal,   &illegal,   &illegal,
                &swc0,      &swc1,      &swc2,      &swc3,      &illegal,    &illegal,   &illegal,   &illegal
        ];

        public static readonly delegate*<Instruction, Assembler, void>[] SpecialLookUpTable = [
                &sll,       &illegal,   &srl,       &sra,       &sllv,      &illegal,   &srlv,      &srav,
                &jr,        &jalr,      &illegal,   &illegal,   &syscall,   &break_,    &illegal,   &illegal,
                &mfhi,      &mthi,      &mflo,      &mtlo,      &illegal,   &illegal,   &illegal,   &illegal,
                &mult,      &multu,     &div,       &divu,      &illegal,   &illegal,   &illegal,   &illegal,
                &add,       &addu,      &sub,       &subu,      &and,       &or,        &xor,       &nor,
                &illegal,   &illegal,   &slt,       &sltu,      &illegal,   &illegal,   &illegal,   &illegal,
                &illegal,   &illegal,   &illegal,   &illegal,   &illegal,   &illegal,   &illegal,   &illegal,
                &illegal,   &illegal,   &illegal,   &illegal,   &illegal,   &illegal,   &illegal,   &illegal
        ];

        private static void illegal(Instruction instruction, Assembler asm) {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("[x64 JIT] Illegal Instruction!");
            Console.ForegroundColor = ConsoleColor.Green;
            throw new Exception("Illegal instruction!");
        }

        private static void special(Instruction instruction, Assembler asm) {
            SpecialLookUpTable[instruction.Get_Subfunction()](instruction, asm);
        }

        private static void bxx(Instruction instruction, Assembler asm) {
            int rs = (int)instruction.Get_rs();
            uint imm = instruction.GetSignedImmediate();
            bool bgez = ((instruction.FullValue >> 16) & 1) == 1;
            bool link = ((instruction.FullValue >> 17) & 0xF) == 0x8;
            x64_JIT.EmitBXX(rs, imm, link, bgez, asm);
        }

        private static void jump(Instruction instruction, Assembler asm) {
            uint target = (CPU_x64_Recompiler.CPU_Struct_Ptr->Next_PC & 0xf0000000) | (instruction.GetImmediateJumpAddress() << 2);
            x64_JIT.EmitJump(target, asm);
        }

        private static void jal(Instruction instruction, Assembler asm) {
            uint target = (CPU_x64_Recompiler.CPU_Struct_Ptr->Next_PC & 0xf0000000) | (instruction.GetImmediateJumpAddress() << 2);
            x64_JIT.EmitJal(target, asm);
        }

        private static void beq(Instruction instruction, Assembler asm) {
            int rs = (int)instruction.Get_rs();
            int rt = (int)instruction.Get_rt();
            uint imm = instruction.GetSignedImmediate();
            x64_JIT.EmitBranchIf(rs, rt, imm, BranchIf.BEQ, asm);
        }

        private static void bne(Instruction instruction, Assembler asm) {
            int rs = (int)instruction.Get_rs();
            int rt = (int)instruction.Get_rt();
            uint imm = instruction.GetSignedImmediate();
            x64_JIT.EmitBranchIf(rs, rt, imm, BranchIf.BNE, asm);
        }

        private static void blez(Instruction instruction, Assembler asm) {
            int rs = (int)instruction.Get_rs();
            uint imm = instruction.GetSignedImmediate();
            x64_JIT.EmitBranchIf(rs, 0, imm, BranchIf.BLEZ, asm);
        }

        private static void bgtz(Instruction instruction, Assembler asm) {
            int rs = (int)instruction.Get_rs();
            uint imm = instruction.GetSignedImmediate();
            x64_JIT.EmitBranchIf(rs, 0, imm, BranchIf.BGTZ, asm);
        }

        private static void addi(Instruction instruction, Assembler asm) {
            int rs = (int)instruction.Get_rs();
            int rt = (int)instruction.Get_rt();
            uint imm = instruction.GetSignedImmediate();
            x64_JIT.EmitArithmetic_i(rs, rt, imm, ArithmeticSignals.ADD, asm);
        }

        private static void addiu(Instruction instruction, Assembler asm) {
            int rs = (int)instruction.Get_rs();
            int rt = (int)instruction.Get_rt();
            uint imm = instruction.GetSignedImmediate();
            x64_JIT.EmitArithmeticI_U(rs, rt, imm, ArithmeticSignals.ADD, asm);
        }

        private static void slti(Instruction instruction, Assembler asm) {
            int rs = (int)instruction.Get_rs();
            int rt = (int)instruction.Get_rt();
            uint imm = instruction.GetSignedImmediate();
            x64_JIT.EmitSlti(rs, rt, imm, true, asm);
        }

        private static void sltiu(Instruction instruction, Assembler asm) {
            int rs = (int)instruction.Get_rs();
            int rt = (int)instruction.Get_rt();
            uint imm = instruction.GetSignedImmediate();
            x64_JIT.EmitSlti(rs, rt, imm, false, asm);
        }

        private static void andi(Instruction instruction, Assembler asm) {
            int rs = (int)instruction.Get_rs();
            int rt = (int)instruction.Get_rt();
            uint imm = instruction.GetImmediate();
            x64_JIT.EmitLogic_i(rs, rt, imm, LogicSignals.AND, asm);
        }

        private static void ori(Instruction instruction, Assembler asm) {
            int rs = (int)instruction.Get_rs();
            int rt = (int)instruction.Get_rt();
            uint imm = instruction.GetImmediate();
            x64_JIT.EmitLogic_i(rs, rt, imm, LogicSignals.OR, asm);
        }

        private static void xori(Instruction instruction, Assembler asm) {
            int rs = (int)instruction.Get_rs();
            int rt = (int)instruction.Get_rt();
            uint imm = instruction.GetImmediate();
            x64_JIT.EmitLogic_i(rs, rt, imm, LogicSignals.XOR, asm);
        }

        private static void lui(Instruction instruction, Assembler asm) {
            int rt = (int)instruction.Get_rt();
            uint imm = instruction.GetImmediate();
            x64_JIT.EmitLUI(rt, imm, asm);
        }

        private static void cop0(Instruction instruction, Assembler asm) {
            int rs = (int)instruction.Get_rs();
            int rt = (int)instruction.Get_rt();
            int rd = (int)instruction.Get_rd();

            switch (rs) {
                case 0b00100:
                    x64_JIT.EmitMTC0(rt, rd, asm);
                    break;

                case 0b00000:
                    x64_JIT.EmitMFC0(rt, rd, asm);
                    break;

                case 0b10000:
                    x64_JIT.EmitRFE(asm);
                    break;
                default: throw new Exception("Unhandled cop0 instruction: " + instruction.FullValue.ToString("X"));
            }
        }

        private static void cop1(Instruction instruction, Assembler asm) {
            throw new Exception("Illegal");
        }

        private static void cop2(Instruction instruction, Assembler asm) {
            if ((instruction.FullValue >> 25) == 0b0100101) {    //COP2 imm25 command
                x64_JIT.EmitCOP2Command(instruction.FullValue, asm);
                return;
            }

            int rs = (int)instruction.Get_rs();
            int rt = (int)instruction.Get_rt();
            int rd = (int)instruction.Get_rd();

            switch (rs) {
                case 0b00000:   //MFC
                    x64_JIT.EmitMFC2_CFC2(rt, rd, asm);
                    break;

                case 0b00010:   //CFC                 
                    x64_JIT.EmitMFC2_CFC2(rt, rd + 32, asm);
                    break;

                case 0b00110:  //CTC                  
                    x64_JIT.EmitMTC2_CTC2(rt, rd + 32, asm);
                    break;

                case 0b00100:  //MTC                  
                    x64_JIT.EmitMTC2_CTC2(rt, rd, asm);
                    break;

                default: throw new Exception("Unhandled GTE opcode: " + instruction.Get_rs().ToString("X"));
            }
        }

        private static void cop3(Instruction instruction, Assembler asm) {
            throw new Exception("Illegal");
        }

        private static void lb (Instruction instruction, Assembler asm) {
            int rs = (int)instruction.Get_rs();
            int rt = (int)instruction.Get_rt();
            uint imm = instruction.GetSignedImmediate();
            x64_JIT.EmitMemoryLoad(rs, rt, imm, MemoryReadWriteSize.BYTE, true, asm);
        }

        private static void lh(Instruction instruction, Assembler asm) {
            int rs = (int)instruction.Get_rs();
            int rt = (int)instruction.Get_rt();
            uint imm = instruction.GetSignedImmediate();
            x64_JIT.EmitMemoryLoad(rs, rt, imm, MemoryReadWriteSize.HALF, true, asm);
        }

        private static void lw(Instruction instruction, Assembler asm) {
            int rs = (int)instruction.Get_rs();
            int rt = (int)instruction.Get_rt();
            uint imm = instruction.GetSignedImmediate();
            x64_JIT.EmitMemoryLoad(rs, rt, imm, MemoryReadWriteSize.WORD, false, asm);
        }

        private static void lbu(Instruction instruction, Assembler asm) {
            int rs = (int)instruction.Get_rs();
            int rt = (int)instruction.Get_rt();
            uint imm = instruction.GetSignedImmediate();
            x64_JIT.EmitMemoryLoad(rs, rt, imm, MemoryReadWriteSize.BYTE, false, asm);
        }

        private static void lhu(Instruction instruction, Assembler asm) {
            int rs = (int)instruction.Get_rs();
            int rt = (int)instruction.Get_rt();
            uint imm = instruction.GetSignedImmediate();
            x64_JIT.EmitMemoryLoad(rs, rt, imm, MemoryReadWriteSize.HALF, false, asm);
        }

        private static void sb(Instruction instruction, Assembler asm) {
            int rs = (int)instruction.Get_rs();
            int rt = (int)instruction.Get_rt();
            uint imm = instruction.GetSignedImmediate();
            x64_JIT.EmitMemoryStore(rs, rt, imm, MemoryReadWriteSize.BYTE, asm);
        }

        private static void sh(Instruction instruction, Assembler asm) {
            int rs = (int)instruction.Get_rs();
            int rt = (int)instruction.Get_rt();
            uint imm = instruction.GetSignedImmediate();
            x64_JIT.EmitMemoryStore(rs, rt, imm, MemoryReadWriteSize.HALF, asm);
        }

        private static void sw(Instruction instruction, Assembler asm) {
            int rs = (int)instruction.Get_rs();
            int rt = (int)instruction.Get_rt();
            uint imm = instruction.GetSignedImmediate();
            x64_JIT.EmitMemoryStore(rs, rt, imm, MemoryReadWriteSize.WORD, asm);
        }

        private static void swl(Instruction instruction, Assembler asm) {
            int rs = (int)instruction.Get_rs();
            int rt = (int)instruction.Get_rt();
            uint imm = instruction.GetSignedImmediate();
            x64_JIT.EmitSWL(rs, rt, imm, asm);
        }

        private static void swr(Instruction instruction, Assembler asm) {
            int rs = (int)instruction.Get_rs();
            int rt = (int)instruction.Get_rt();
            uint imm = instruction.GetSignedImmediate();
            x64_JIT.EmitSWR(rs, rt, imm, asm);
        }

        private static void lwl(Instruction instruction, Assembler asm) {
            int rs = (int)instruction.Get_rs();
            int rt = (int)instruction.Get_rt();
            uint imm = instruction.GetSignedImmediate();
            x64_JIT.EmitLWL(rs, rt, imm, asm);
        }

        private static void lwr(Instruction instruction, Assembler asm) {
            int rs = (int)instruction.Get_rs();
            int rt = (int)instruction.Get_rt();
            uint imm = instruction.GetSignedImmediate();
            x64_JIT.EmitLWR(rs, rt, imm, asm);
        }

        private static void lwc0(Instruction instruction, Assembler asm) {
            throw new Exception("Illegal");
        }

        private static void lwc1(Instruction instruction, Assembler asm) {
            throw new Exception("Illegal");
        }

        private static void lwc2(Instruction instruction, Assembler asm) {
            int rs = (int)instruction.Get_rs();
            int rt = (int)instruction.Get_rt();
            uint imm = instruction.GetSignedImmediate();
            x64_JIT.EmitLWC2(rs, rt, imm, asm);
        }

        private static void lwc3(Instruction instruction, Assembler asm) {
            throw new Exception("Illegal");
        }

        private static void swc0(Instruction instruction, Assembler asm) {
            throw new Exception("Illegal");
        }

        private static void swc1(Instruction instruction, Assembler asm) {
            throw new Exception("Illegal");
        }

        private static void swc2(Instruction instruction, Assembler asm) {
            int rs = (int)instruction.Get_rs();
            int rt = (int)instruction.Get_rt();
            uint imm = instruction.GetSignedImmediate();
            x64_JIT.EmitSWC2(rs, rt, imm, asm);
        }

        private static void swc3(Instruction instruction, Assembler asm) {
            throw new Exception("Illegal");
        }

        private static void sll(Instruction instruction, Assembler asm) {
            int rt = (int)instruction.Get_rt();
            int rd = (int)instruction.Get_rd();
            uint sa = instruction.Get_sa();
            x64_JIT.EmitShift(rt, rd, sa, ShiftSignals.LEFT, asm);
        }

        private static void srl(Instruction instruction, Assembler asm) {
            int rt = (int)instruction.Get_rt();
            int rd = (int)instruction.Get_rd();
            uint sa = instruction.Get_sa();
            x64_JIT.EmitShift(rt, rd, sa, ShiftSignals.RIGHT, asm);
        }

        private static void sra(Instruction instruction, Assembler asm) {
            int rt = (int)instruction.Get_rt();
            int rd = (int)instruction.Get_rd();
            uint sa = instruction.Get_sa();
            x64_JIT.EmitShift(rt, rd, sa, ShiftSignals.RIGHT_ARITHMETIC, asm);
        }

        private static void sllv(Instruction instruction, Assembler asm) {
            int rs = (int)instruction.Get_rs();
            int rt = (int)instruction.Get_rt();
            int rd = (int)instruction.Get_rd();
            x64_JIT.EmitShift_v(rs, rt, rd, ShiftSignals.LEFT, asm);
        }

        private static void srlv(Instruction instruction, Assembler asm) {
            int rs = (int)instruction.Get_rs();
            int rt = (int)instruction.Get_rt();
            int rd = (int)instruction.Get_rd();
            x64_JIT.EmitShift_v(rs, rt, rd, ShiftSignals.RIGHT, asm);
        }

        private static void srav(Instruction instruction, Assembler asm) {
            int rs = (int)instruction.Get_rs();
            int rt = (int)instruction.Get_rt();
            int rd = (int)instruction.Get_rd();
            x64_JIT.EmitShift_v(rs, rt, rd, ShiftSignals.RIGHT_ARITHMETIC, asm);
        }

        private static void jr(Instruction instruction, Assembler asm) {
            int rs = (int)instruction.Get_rs();
            x64_JIT.EmitJR(rs, asm);
        }

        private static void jalr(Instruction instruction, Assembler asm) {
            int rs = (int)instruction.Get_rs();
            int rd = (int)instruction.Get_rd();
            x64_JIT.EmitJalr(rs, rd, asm);
        }

        private static void syscall(Instruction instruction, Assembler asm) {
            x64_JIT.EmitSyscall(asm);
        }

        private static void break_(Instruction instruction, Assembler asm) {
            x64_JIT.EmitBreak(asm);
        }

        private static void mfhi(Instruction instruction, Assembler asm) {
            int rd = (int)instruction.Get_rd();
            x64_JIT.EmitMF(rd, true, asm);
        }

        private static void mthi(Instruction instruction, Assembler asm) {
            int rs = (int)instruction.Get_rs();
            x64_JIT.EmitMT(rs, true, asm);
        }

        private static void mflo(Instruction instruction, Assembler asm) {
            int rd = (int)instruction.Get_rd();
            x64_JIT.EmitMF(rd, false, asm);
        }

        private static void mtlo(Instruction instruction, Assembler asm) {
            int rs = (int)instruction.Get_rs();
            x64_JIT.EmitMT(rs, false, asm);
        }

        private static void mult(Instruction instruction, Assembler asm) {
            int rs = (int)instruction.Get_rs();
            int rt = (int)instruction.Get_rt();
            x64_JIT.EmitMULT(rs, rt, true, asm);
        }

        private static void multu(Instruction instruction, Assembler asm) {
            int rs = (int)instruction.Get_rs();
            int rt = (int)instruction.Get_rt();
            x64_JIT.EmitMULT(rs, rt, false, asm);
        }

        private static void div(Instruction instruction, Assembler asm) {
            int rs = (int)instruction.Get_rs();
            int rt = (int)instruction.Get_rt();
            x64_JIT.EmitDIV(rs, rt, true, asm);
        }

        private static void divu(Instruction instruction, Assembler asm) {
            int rs = (int)instruction.Get_rs();
            int rt = (int)instruction.Get_rt();
            x64_JIT.EmitDIV(rs, rt, false, asm);
        }

        private static void add(Instruction instruction, Assembler asm) {
            int rd = (int)instruction.Get_rd();
            int rs = (int)instruction.Get_rs();
            int rt = (int)instruction.Get_rt();
            x64_JIT.EmitArithmetic(rs, rt, rd, ArithmeticSignals.ADD, asm);
        }

        private static void addu(Instruction instruction, Assembler asm) {
            int rd = (int)instruction.Get_rd();
            int rs = (int)instruction.Get_rs();
            int rt = (int)instruction.Get_rt();
            x64_JIT.EmitArithmeticU(rs, rt, rd, ArithmeticSignals.ADD, asm);
        }

        private static void subu(Instruction instruction, Assembler asm) {
            int rd = (int)instruction.Get_rd();
            int rs = (int)instruction.Get_rs();
            int rt = (int)instruction.Get_rt();
            x64_JIT.EmitArithmeticU(rs, rt, rd, ArithmeticSignals.SUB, asm);
        }

        private static void sub(Instruction instruction, Assembler asm) {
            int rd = (int)instruction.Get_rd();
            int rs = (int)instruction.Get_rs();
            int rt = (int)instruction.Get_rt();
            x64_JIT.EmitArithmetic(rs, rt, rd, ArithmeticSignals.SUB, asm);
        }

        public static void or(Instruction instruction, Assembler asm) {
            int rd = (int)instruction.Get_rd();
            int rs = (int)instruction.Get_rs();
            int rt = (int)instruction.Get_rt();
            x64_JIT.EmitLogic(rs, rt, rd, LogicSignals.OR, asm);
        }

        private static void and(Instruction instruction, Assembler asm) {
            int rd = (int)instruction.Get_rd();
            int rs = (int)instruction.Get_rs();
            int rt = (int)instruction.Get_rt();
            x64_JIT.EmitLogic(rs, rt, rd, LogicSignals.AND, asm);
        }

        private static void xor(Instruction instruction, Assembler asm) {
            int rd = (int)instruction.Get_rd();
            int rs = (int)instruction.Get_rs();
            int rt = (int)instruction.Get_rt();
            x64_JIT.EmitLogic(rs, rt, rd, LogicSignals.XOR, asm);
        }

        private static void nor(Instruction instruction, Assembler asm) {
            int rd = (int)instruction.Get_rd();
            int rs = (int)instruction.Get_rs();
            int rt = (int)instruction.Get_rt();
            x64_JIT.EmitLogic(rs, rt, rd, LogicSignals.NOR, asm);
        }

        private static void slt(Instruction instruction, Assembler asm) {
            int rd = (int)instruction.Get_rd();
            int rs = (int)instruction.Get_rs();
            int rt = (int)instruction.Get_rt();
            x64_JIT.EmitSlt(rs, rt, rd, true, asm);
        }

        private static void sltu(Instruction instruction, Assembler asm) {
            int rd = (int)instruction.Get_rd();
            int rs = (int)instruction.Get_rs();
            int rt = (int)instruction.Get_rt();
            x64_JIT.EmitSlt(rs, rt, rd, false, asm);
        }
    }
}
