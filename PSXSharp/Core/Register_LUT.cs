using System;
using System.Diagnostics;
using Instruction = PSXSharp.Core.Common.Instruction;

namespace PSXSharp.Core {
    public static unsafe class Register_LUT {

        //Returns the registers used by instructions, with the write target being always at index 0
        //If there is no write (or it is not to a GPR) then index 0 contains 0
        //Read registers are at index 1 and above

        public static readonly delegate*<Instruction, uint[]>[] MainLookUpTable = [
                &special,   &bxx,       &jump,      &jal,       &beq,        &bne,       &blez,      &bgtz,
                &addi,      &addiu,     &slti,      &sltiu,     &andi,       &ori,       &xori,      &lui,
                &cop0,      &cop1,      &cop2,      &cop3,      &illegal,    &illegal,   &illegal,   &illegal,
                &illegal,   &illegal,   &illegal,   &illegal,   &illegal,    &illegal,   &illegal,   &illegal,
                &lb,        &lh,        &lwl,       &lw,        &lbu,        &lhu,       &lwr,       &illegal,
                &sb,        &sh,        &swl,       &sw,        &illegal,    &illegal,   &swr,       &illegal,
                &lwc0,      &lwc1,      &lwc2,      &lwc3,      &illegal,    &illegal,   &illegal,   &illegal,
                &swc0,      &swc1,      &swc2,      &swc3,      &illegal,    &illegal,   &illegal,   &illegal
        ];

        public static readonly delegate*<Instruction, uint[]>[] SpecialLookUpTable = [
                &sll,       &illegal,   &srl,       &sra,       &sllv,      &illegal,   &srlv,      &srav,
                &jr,        &jalr,      &illegal,   &illegal,   &syscall,   &break_,    &illegal,   &illegal,
                &mfhi,      &mthi,      &mflo,      &mtlo,      &illegal,   &illegal,   &illegal,   &illegal,
                &mult,      &multu,     &div,       &divu,      &illegal,   &illegal,   &illegal,   &illegal,
                &add,       &addu,      &sub,       &subu,      &and,       &or,        &xor,       &nor,
                &illegal,   &illegal,   &slt,       &sltu,      &illegal,   &illegal,   &illegal,   &illegal,
                &illegal,   &illegal,   &illegal,   &illegal,   &illegal,   &illegal,   &illegal,   &illegal,
                &illegal,   &illegal,   &illegal,   &illegal,   &illegal,   &illegal,   &illegal,   &illegal
        ];

        private static uint[] illegal(Instruction instruction) {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("[Register LUT] Illegal instruction");
            Console.ForegroundColor = ConsoleColor.Green;
            throw new UnreachableException();
        }

        private static uint[] special(Instruction instruction) {
            return SpecialLookUpTable[instruction.Sub](instruction);
        }

        private static uint[] bxx(Instruction instruction) {
            uint rs = instruction.Rs;
            bool link = (instruction.Value >> 17 & 0xF) == 0x8;
            return [(uint)(link? 31:0), rs];
        }

        private static uint[] jump(Instruction instruction) {
            return [0];
        }

        private static uint[] jal(Instruction instruction) {
            return [31];
        }

        private static uint[] beq(Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            return [0, rt, rs];
        }

        private static uint[] bne(Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            return [0, rt, rs];
        }

        private static uint[] blez(Instruction instruction) {
            uint rs = instruction.Rs;
            return [0, rs];
        }

        private static uint[] bgtz(Instruction instruction) {
            uint rs = instruction.Rs;
            return [0, rs];
        }

        private static uint[] addi(Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            return [rt, rs];
        }

        private static uint[] addiu(Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            return [rt, rs];
        }

        private static uint[] slti(Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            return [rt, rs];
        }

        private static uint[] sltiu(Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            return [rt, rs];
        }

        private static uint[] andi(Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            return [rt, rs];
        }

        private static uint[] ori(Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            return [rt, rs];
        }

        private static uint[] xori(Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            return [rt, rs];
        }

        private static uint[] lui(Instruction instruction) {
            uint rt = instruction.Rt;
            return [rt];
        }

        private static uint[] cop0(Instruction instruction) {
            uint rt = instruction.Rt;
            uint rd = instruction.Rd;
            uint rs = instruction.Rs;

            //Ensure that the first index is the GPR write target
            //If the read/write target is in COP0 we replace it with 0

            switch (rs) {
                case 0b00100:  // MTC0
                    return [0, rt];        

                case 0b00000:  // MFC0
                    return [rt];     

                case 0b10000:   // RFE
                    return [0];
                default:
                    throw new Exception("Unhandled cop0 instruction: " + instruction.Value.ToString("X"));
            }
        }

        private static uint[] cop1(Instruction instruction) {
            throw new Exception("Illegal");
        }

        private static uint[] cop2(Instruction instruction) {
            if (instruction.Value >> 25 == 0b0100101) {
                return [0];
            }

            uint rt = instruction.Rt;
            uint rd = instruction.Rd;
            uint rs = instruction.Rs;

            //Ensure that the first index is the GPR write target
            //If the read/write target is in COP2 we replace it with 0

            switch (rs) {
                case 0b00000:   // MFC2
                case 0b00010:   // CFC2
                    return [rt];

                case 0b00110:   // CTC2                  
                case 0b00100:   // MTC2
                    return [0, rt];
                default:
                    throw new Exception("Unhandled GTE opcode: " + instruction.Rs.ToString("X"));
            }
        }

        private static uint[] cop3(Instruction instruction) {
            throw new Exception("Illegal");
        }

        private static uint[] lb(Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            return [rt, rs];
        }

        private static uint[] lh(Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            return [rt, rs];
        }

        private static uint[] lw(Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            return [rt, rs];
        }

        private static uint[] lbu(Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            return [rt, rs];
        }

        private static uint[] lhu(Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            return [rt, rs];
        }

        private static uint[] sb(Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            return [0, rt, rs];
        }

        private static uint[] sh(Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            return [0, rt, rs];
        }

        private static uint[] sw(Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            return [0, rt, rs];
        }

        private static uint[] swl(Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            return [0, rt, rs];
        }

        private static uint[] swr(Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            return [0, rt, rs];
        }

        private static uint[] lwl(Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            return [rt, rs];
        }

        private static uint[] lwr(Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            return [rt, rs];
        }

        private static uint[] lwc0(Instruction instruction) {
            throw new Exception("Illegal");
        }

        private static uint[] lwc1(Instruction instruction) {
            throw new Exception("Illegal");
        }

        private static uint[] lwc2(Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            return [0, rs];
        }

        private static uint[] lwc3(Instruction instruction) {
            throw new Exception("Illegal");
        }

        private static uint[] swc0(Instruction instruction) {
            throw new Exception("Illegal");
        }

        private static uint[] swc1(Instruction instruction) {
            throw new Exception("Illegal");
        }

        private static uint[] swc2(Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            return [0, rs];
        }

        private static uint[] swc3(Instruction instruction) {
            throw new Exception("Illegal");
        }

        private static uint[] sll(Instruction instruction) {
            uint rt = instruction.Rt;
            uint rd = instruction.Rd;
            return [rd, rt];
        }

        private static uint[] srl(Instruction instruction) {
            uint rt = instruction.Rt;
            uint rd = instruction.Rd;
            return [rd, rt];
        }

        private static uint[] sra(Instruction instruction) {
            uint rt = instruction.Rt;
            uint rd = instruction.Rd;
            return [rd, rt];
        }

        private static uint[] sllv(Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            uint rd = instruction.Rd;
            return [rd, rt, rs];
        }

        private static uint[] srlv(Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            uint rd = instruction.Rd;
            return [rd, rt, rs];
        }

        private static uint[] srav(Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            uint rd = instruction.Rd;
            return [rd, rt, rs];
        }

        private static uint[] jr(Instruction instruction) {
            uint rs = instruction.Rs;
            return [0, rs];
        }

        private static uint[] jalr(Instruction instruction) {
            uint rs = instruction.Rs;
            uint rd = instruction.Rd;
            return [rd, rs];
        }

        private static uint[] syscall(Instruction instruction) {
            return [0];
        }

        private static uint[] break_(Instruction instruction) {
            return [0];
        }

        private static uint[] mfhi(Instruction instruction) {
            uint rd = instruction.Rd;
            return [rd];
        }

        private static uint[] mthi(Instruction instruction) {
            uint rs = instruction.Rs;
            return [0, rs];
        }

        private static uint[] mflo(Instruction instruction) {
            uint rd = instruction.Rd;
            return [rd];
        }

        private static uint[] mtlo(Instruction instruction) {
            uint rs = instruction.Rs;
            return [0, rs];
        }

        private static uint[] mult(Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            return [0, rt, rs];
        }

        private static uint[] multu(Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            return [0, rt, rs];
        }

        private static uint[] div(Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            return [0, rt, rs];
        }

        private static uint[] divu(Instruction instruction) {
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            return [0, rt, rs];
        }

        private static uint[] add(Instruction instruction) {
            uint rd = instruction.Rd;
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            return [rd, rt, rs];
        }

        private static uint[] addu(Instruction instruction) {
            uint rd = instruction.Rd;
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            return [rd, rt, rs];
        }

        private static uint[] subu(Instruction instruction) {
            uint rd = instruction.Rd;
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            return [rd, rt, rs];
        }

        private static uint[] sub(Instruction instruction) {
            uint rd = instruction.Rd;
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            return [rd, rt, rs];
        }

        private static uint[] or(Instruction instruction) {
            uint rd = instruction.Rd;
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            return [rd, rt, rs];
        }

        private static uint[] and(Instruction instruction) {
            uint rd = instruction.Rd;
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            return [rd, rt, rs];
        }

        private static uint[] xor(Instruction instruction) {
            uint rd = instruction.Rd;
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            return [rd, rt, rs];
        }

        private static uint[] nor(Instruction instruction) {
            uint rd = instruction.Rd;
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            return [rd, rt, rs];
        }

        private static uint[] slt(Instruction instruction) {
            uint rd = instruction.Rd;
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            return [rd, rt, rs];
        }

        private static uint[] sltu(Instruction instruction) {
            uint rd = instruction.Rd;
            uint rs = instruction.Rs;
            uint rt = instruction.Rt;
            return [rd, rt, rs];
        }
    }
}