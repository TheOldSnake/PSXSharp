using System;
using System.IO;
using System.Runtime.InteropServices;
using PSXSharp.Core.Common;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;
using static PSXSharp.Core.x64_Recompiler.AddressGetter;
using System.Diagnostics;

namespace PSXSharp.Core.x64_Recompiler {

    //Implements R3000 MIPS instructions in x64 assembly, intended for windows x64
    public static unsafe class x64_JIT {
        //Register usage:
        //RBX -> Base CPU struct address
        //EAX, ECX, EDX, EDI, ESI, R8 -> General Calculations
        //R15 -> Loading function pointers
        //R12 - R14 Callee-saved registers

        #region Offsets
        private static int GPR_Offset = (int)Marshal.OffsetOf<CPUNativeStruct>(nameof(CPUNativeStruct.GPR));

        private static int BranchFlagOffset = (int)Marshal.OffsetOf<CPUNativeStruct>(nameof(CPUNativeStruct.Branch));
        private static int DelaySlotOffset = (int)Marshal.OffsetOf<CPUNativeStruct>(nameof(CPUNativeStruct.DelaySlot));
        private static int PCOffset = (int)Marshal.OffsetOf<CPUNativeStruct>(nameof(CPUNativeStruct.PC));
        private static int NextPCOffset = (int)Marshal.OffsetOf<CPUNativeStruct>(nameof(CPUNativeStruct.Next_PC));
        private static int CurrentPCOffset = (int)Marshal.OffsetOf<CPUNativeStruct>(nameof(CPUNativeStruct.Current_PC));

        //Offsets of number and value inside the RegisterLoad struct
        private static int Number_offset = (int)Marshal.OffsetOf<CPU.RegisterLoad>(nameof(CPU.RegisterLoad.RegisterNumber));
        private static int Value_offset = (int)Marshal.OffsetOf<CPU.RegisterLoad>(nameof(CPU.RegisterLoad.Value));

        //Offsets of RegisterLoad structs in cpu struct
        private static int ReadyRegisterLoad = (int)Marshal.OffsetOf<CPUNativeStruct>(nameof(CPUNativeStruct.ReadyLoad));
        private static int DelayedRegisterLoad = (int)Marshal.OffsetOf<CPUNativeStruct>(nameof(CPUNativeStruct.DelayedLoad));
        private static int DirectWrite = (int)Marshal.OffsetOf<CPUNativeStruct>(nameof(CPUNativeStruct.DirectLoad));

        //Final offsets of DelayLoads
        private static int ReadyRegisterLoad_number = ReadyRegisterLoad + Number_offset;
        private static int ReadyRegisterLoad_value = ReadyRegisterLoad + Value_offset;
        private static int DelayedRegisterLoad_number = DelayedRegisterLoad + Number_offset;
        private static int DelayedRegisterLoad_value = DelayedRegisterLoad + Value_offset;
        private static int DirectWrite_number = DirectWrite + Number_offset;
        private static int DirectWrite_value = DirectWrite + Value_offset;

        private static int LO_Offset = (int)Marshal.OffsetOf<CPUNativeStruct>(nameof(CPUNativeStruct.LO));
        private static int HI_Offset = (int)Marshal.OffsetOf<CPUNativeStruct>(nameof(CPUNativeStruct.HI));

        private static int COP0_SR_Offset = (int)Marshal.OffsetOf<CPUNativeStruct>(nameof(CPUNativeStruct.COP0_SR));
        private static int COP0_Cause_Offset = (int)Marshal.OffsetOf<CPUNativeStruct>(nameof(CPUNativeStruct.COP0_Cause));
        private static int COP0_EPC_Offset = (int)Marshal.OffsetOf<CPUNativeStruct>(nameof(CPUNativeStruct.COP0_EPC));

        private static int CurrentCycle_Offset = (int)Marshal.OffsetOf<CPUNativeStruct>(nameof(CPUNativeStruct.CurrentCycle));
        #endregion

        //Prints a register value to the console
        //Destroys ecx!
        private static void EmitPrintReg(Assembler asm, AssemblerRegister32 src) {
            asm.sub(rsp, 40);                       //Shadow space
            asm.mov(ecx, src);
            asm.mov(r15, GetPrintAddress());
            asm.call(r15);
            asm.add(rsp, 40);                       //Undo Shadow space 
        }

        public static bool EnableLoadDelaySlot = false;
        public static bool IsFirstInstruction = false;

        private static void EmitRegisterRead(Assembler asm, AssemblerRegister32 dst, int srcNumber) {
            //asm.mov(r15, GetGPRAddress(srcNumber));     //We use r15 ONLY for holding 64-bit addresses
            int regOffset = GPR_Offset + (srcNumber * 4);
            asm.mov(dst, __dword_ptr[rbx + regOffset]);
        }

        private static void EmitRegisterWrite(Assembler asm, int dstNumber, AssemblerRegister32 src, bool delayed) {
            int regNumber_Offset;
            int regValue_Offset;
            if (EnableLoadDelaySlot) {
                if (delayed) {
                    regNumber_Offset = DelayedRegisterLoad_number;
                    regValue_Offset = DelayedRegisterLoad_value;
                } else {
                    regNumber_Offset = DirectWrite_number;
                    regValue_Offset = DirectWrite_value;
                }

                asm.mov(__dword_ptr[rbx + regNumber_Offset], dstNumber);
                asm.mov(__dword_ptr[rbx + regValue_Offset], src);

            } else {
                int destOffset = GPR_Offset + (dstNumber * 4);
                if (destOffset != 0) {
                    asm.mov(__dword_ptr[rbx + destOffset], src);
                }            
            }
        }

        private static void EmitRegisterWrite(Assembler asm, int dstNumber, uint imm) {
            if (EnableLoadDelaySlot) {
                int regNumber_Offset = DirectWrite_number;
                int regValue_Offset = DirectWrite_value;
                asm.mov(__dword_ptr[rbx + regNumber_Offset], dstNumber);
                asm.mov(__dword_ptr[rbx + regValue_Offset], imm);
            } else {
                int destOffset = GPR_Offset + (dstNumber * 4);
                if (destOffset != 0) {
                    asm.mov(__dword_ptr[rbx + destOffset], imm);
                }            
            }
        }

        public static void MaybeCancelLoadDelay(Assembler asm, int target) {
            //This is a combination of runtime and compile time check after the first instruction
            //that handles a possible delayed load from the last instruction in the previous block
            Label skip = asm.CreateLabel();

            //If there was a delayed load, the register number is now in ReadyRegisterLoad_number
            asm.mov(eax, __dword_ptr[rbx + ReadyRegisterLoad_number]);  

            //If it's zero, then there is no delayed load
            asm.test(eax, eax);             
            asm.jz(skip);

            //If it equals to this block's first instruction (direct) write target, we cancel the load
            if (target != 0) {
                asm.cmp(eax, target);     
                asm.je(skip);
            }

            //Otherwise do the load
            asm.mov(ecx, __dword_ptr[rbx + ReadyRegisterLoad_value]);
            asm.shl(eax, 2);

            if (GPR_Offset > 0) {
                asm.mov(__dword_ptr[rbx + rax + GPR_Offset], ecx);
            } else {
                asm.mov(__dword_ptr[rbx + rax], ecx);
            }

            asm.Label(ref skip);

            //Write zero as the load has either been written or canceled
            asm.mov(__qword_ptr[rbx + ReadyRegisterLoad_number], 0);
        }

        private static void EmitCheckCacheIsolation(Assembler asm) {
            //IscIsolateCache => (Cop0.SR & 0x10000) != 0
            asm.bt(__dword_ptr[rbx + COP0_SR_Offset], 16);       //BT = Bit Test, the CF flag contains the value of the selected bit
        }

        public static void EmitSyscall(Assembler asm) {
            //Call Exception function with Syscall Value (0x8) 
            asm.mov(r15, GetExceptionAddress());    //Load function pointer
            asm.mov(rcx, GetCPUStructAddress());    //First Parameter
            asm.mov(edx, 0x8);                      //Second Parameter
            asm.call(r15);                          //Call Exception
        }

        public static void EmitBreak(Assembler asm) {
            //Call Exception function with Break Value (0x9) 
            asm.mov(r15, GetExceptionAddress());    //Load function pointer
            asm.mov(rcx, GetCPUStructAddress());    //First Parameter
            asm.mov(edx, 0x9);                      //Second Parameter
            asm.call(r15);                          //Call Exception
        }

        public static void EmitSlti(int rs, int rt, uint imm, bool signed, Assembler asm) {
            asm.mov(eax, 0);                                //Set result to 0 initially
            EmitRegisterRead(asm, ecx, rs);                 //Load GPR[rs]
            asm.mov(edx, imm);                              //Load imm
            asm.cmp(ecx, edx);                              //Compare 

            if (signed) {
                asm.setl(al);                               //Setl for signed
            } else {
                asm.setb(al);                               //Setb for unsigned
            }

            asm.movzx(eax, al);

            //Write to GPR[rt]
            EmitRegisterWrite(asm, rt, eax, false);
        }

        public static void EmitSlt(int rs, int rt, int rd, bool signed, Assembler asm) {
            asm.mov(eax, 0);                                //Set result to 0 initially
            EmitRegisterRead(asm, ecx, rs);                 //Load GPR[rs]
            EmitRegisterRead(asm, edx, rt);                 //Load GPR[rt]
            asm.cmp(ecx, edx);                              //Compare 

            if (signed) {
                asm.setl(al);                               //Setl for signed
            } else {
                asm.setb(al);                               //Setb for unsigned
            }

            asm.movzx(eax, al);

            //Write to GPR[rd]
            EmitRegisterWrite(asm, rd, eax, false);
        }

        public static void EmitBranchIf(int rs, int rt, uint imm, int type, Assembler asm) {
            Label skipBranch = asm.CreateLabel();

            EmitRegisterRead(asm, ecx, rs);                                 //Load GPR[rs]

            if (type > 1) {
                asm.mov(edx, 0);                                            //Load 0, ignore the rt parameter 
            } else {
                EmitRegisterRead(asm, edx, rt);                             //Load GPR[rt]
            }

            asm.cmp(ecx, edx);                                              //Compare

            //The inverse means we don't branch
            switch (type) {
                //0,1 are comparing with GPR[rt] 
                case BranchIf.BEQ: asm.jne(skipBranch); break;
                case BranchIf.BNE: asm.je(skipBranch); break;

                //2,3 are comparing with constant 0 
                case BranchIf.BLEZ: asm.jg(skipBranch); break;
                case BranchIf.BGTZ: asm.jle(skipBranch); break;

                default: throw new Exception("Invalid Type: " + type);
            }

            //If no jump happens we branch
            EmitBranch(asm, imm);

            //if we jump to this we continue normally
            asm.Label(ref skipBranch);

            //No link for these instructions
        }

        public static void EmitJalr(int rs, int rd, Assembler asm) {
            //Store return address in GRR[rd]
            asm.mov(ecx, __dword_ptr[rbx + NextPCOffset]);
            EmitRegisterWrite(asm, rd, ecx, false);

            //Jump to address in GRR[rs]
            EmitRegisterRead(asm, ecx, rs);
            asm.mov(__dword_ptr[rbx + NextPCOffset], ecx);
            asm.mov(__dword_ptr[rbx + BranchFlagOffset], 1);
        }

        public static void EmitJR(int rs, Assembler asm) {
            //Jump to address in GRR[rs]
            EmitRegisterRead(asm, ecx, rs);
            asm.mov(__dword_ptr[rbx + NextPCOffset], ecx);
            asm.mov(__dword_ptr[rbx + BranchFlagOffset], 1);
        }

        public static void EmitJal(uint targetAddress, Assembler asm) {
            //Link to reg 31
            asm.mov(ecx, __dword_ptr[rbx + NextPCOffset]);
            EmitRegisterWrite(asm, (int)CPU.Register.ra, ecx, false);

            //Jump to target
            asm.mov(__dword_ptr[rbx + NextPCOffset], targetAddress);
            asm.mov(__dword_ptr[rbx + BranchFlagOffset], 1);
        }

        public static void EmitJump(uint targetAddress, Assembler asm) {
            //Jump to target
            asm.mov(__dword_ptr[rbx + NextPCOffset], targetAddress);
            asm.mov(__dword_ptr[rbx + BranchFlagOffset], 1);
        }

        public static void EmitBXX(int rs, uint imm, bool link, bool bgez, Assembler asm) {
            Label skipBranch = asm.CreateLabel();

            asm.mov(r8d, __dword_ptr[rbx + NextPCOffset]);        //Save a copy of next pc
            EmitRegisterRead(asm, ecx, rs);                       //Read GPR[rs]
            asm.mov(edx, 0);                                      //Load 0
            asm.cmp(ecx, edx);                                    //Compare

            //Test the inverse, if true then we don't branch
            if (bgez) {
                asm.jl(skipBranch);
            } else {
                asm.jge(skipBranch);
            }

            EmitBranch(asm, imm);

            asm.Label(ref skipBranch);

            if (link) {
                //link to reg 31
                EmitRegisterWrite(asm, (int)CPU.Register.ra, r8d, false);
            }
        }

        public static void EmitArithmeticU(int rs, int rt, int rd, int type, Assembler asm) {
            //We just emit normal add since we don't check for overflows anyway
            EmitArithmetic(rs, rt, rd, type, asm);
        }

        public static void EmitArithmeticI_U(int rs, int rt, uint imm, int type, Assembler asm) {
            //We just emit normal addi since we don't check for overflows anyway
            EmitArithmetic_i(rs, rt, imm, type, asm);
        }

        public static void EmitArithmetic_i(int rs, int rt, uint imm, int type, Assembler asm) {
            //This should check for signed overflow, but it can be ignored as no games rely on this 

            if (type == ArithmeticSignals.SUB) {
                //subi/subiu are pseudo-instructions, and are done using addi/addiu -- we shouldn't get this here
                Console.WriteLine("Got subi/subiu pseudo-instructions!");
                throw new UnreachableException();
            }

            //If rs is $0 then this is a simple move instruction
            if (rs == 0) {
                EmitRegisterWrite(asm, rt, imm);
                return;
            }

            EmitRegisterRead(asm, eax, rs);
            asm.add(eax, imm);
            EmitRegisterWrite(asm, rt, eax, false);
        }

        public static void EmitArithmetic(int rs, int rt, int rd, int type, Assembler asm) {
            //This should check for signed overflow, but it can be ignored as no games rely on this 
            EmitRegisterRead(asm, eax, rs);
            EmitRegisterRead(asm, ecx, rt);

            switch (type) {
                case ArithmeticSignals.ADD: asm.add(eax, ecx); break;
                case ArithmeticSignals.SUB: asm.sub(eax, ecx); break;
                default: throw new Exception("JIT: Unknown Arithmetic_i : " + type);
            }

            EmitRegisterWrite(asm, rd, eax, false);
        }

        public static void EmitLogic_i(int rs, int rt, uint imm, int type, Assembler asm) {
            EmitRegisterRead(asm, eax, rs);

            //Emit the required op
            switch (type) {
                case LogicSignals.AND: asm.and(eax, imm); break;
                case LogicSignals.OR: asm.or(eax, imm); break;
                case LogicSignals.XOR: asm.xor(eax, imm); break;
                //There is no NORI instruction
                default: throw new Exception("JIT: Unknown Logic_i : " + type);
            }

            EmitRegisterWrite(asm, rt, eax, false);
        }

        public static void EmitLogic(int rs, int rt, int rd, int type, Assembler asm) {
            EmitRegisterRead(asm, eax, rs);
            EmitRegisterRead(asm, ecx, rt);

            //Emit the required op
            switch (type) {
                case LogicSignals.AND: asm.and(eax, ecx); break;
                case LogicSignals.OR: asm.or(eax, ecx); break;
                case LogicSignals.XOR: asm.xor(eax, ecx); break;
                case LogicSignals.NOR:
                    asm.or(eax, ecx);
                    asm.not(eax);
                    break;
                default: throw new Exception("JIT: Unknown Logic: " + type);
            }

            EmitRegisterWrite(asm, rd, eax, false);
        }

        public static void EmitShift(int rt, int rd, uint amount, uint direction, Assembler asm) {
            EmitRegisterRead(asm, eax, rt);

            switch (direction) {
                case ShiftSignals.LEFT: asm.shl(eax, (byte)amount); break;
                case ShiftSignals.RIGHT: asm.shr(eax, (byte)amount); break;
                case ShiftSignals.RIGHT_ARITHMETIC: asm.sar(eax, (byte)amount); break;
                default: throw new Exception("Unknown Shift direction");
            }

            EmitRegisterWrite(asm, rd, eax, false);
        }

        public static void EmitShift_v(int rs, int rt, int rd, int direction, Assembler asm) {
            EmitRegisterRead(asm, eax, rt);
            EmitRegisterRead(asm, ecx, rs);
            asm.and(ecx, 0x1F);            //The shift amount (rs value) has to be masked with 0x1F

            //register cl contains low byte of ecx
            switch (direction) {
                case ShiftSignals.LEFT: asm.shl(eax, cl); break;
                case ShiftSignals.RIGHT: asm.shr(eax, cl); break;
                case ShiftSignals.RIGHT_ARITHMETIC: asm.sar(eax, cl); break;
                default: throw new Exception("Unknown Shift direction");
            }

            EmitRegisterWrite(asm, rd, eax, false);
        }

        public static void EmitDIV(int rs, int rt, bool signed, Assembler asm) {
            Label check2 = asm.CreateLabel();
            Label check3 = asm.CreateLabel();
            Label normalCase = asm.CreateLabel();
            Label end = asm.CreateLabel();

            EmitRegisterRead(asm, eax, rs);     //numerator
            EmitRegisterRead(asm, ecx, rt);     //denominator

            //This could be optimized
            if (signed) {
                //If numerator >= 0 && denominator == 0:
                //Check the inverse
                asm.mov(esi, 0);
                asm.cmp(eax, esi);
                asm.jl(check2);
                asm.cmp(ecx, esi);
                asm.jne(check2);

                //LO = 0xffffffff;
                //HI = (uint)numerator;
                asm.mov(__dword_ptr[rbx + LO_Offset], 0xffffffff);
                asm.mov(__dword_ptr[rbx + HI_Offset], eax);
                asm.jmp(end);


                asm.Label(ref check2);

                //If numerator < 0 && denominator == 0
                //Check the inverse
                //esi already 0
                asm.cmp(eax, esi);
                asm.jge(check3);
                asm.cmp(ecx, esi);
                asm.jne(check3);

                //LO = 1;
                //HI = (uint)numerator;
                asm.mov(__dword_ptr[rbx + LO_Offset], 1);
                asm.mov(__dword_ptr[rbx + HI_Offset], eax);
                asm.jmp(end);

                asm.Label(ref check3);

                //If numerator == 0x80000000 && denominator == 0xffffffff
                //Check the inverse
                asm.mov(esi, 0x80000000);
                asm.cmp(eax, esi);
                asm.jne(normalCase);
                asm.mov(esi, 0xffffffff);
                asm.cmp(ecx, esi);
                asm.jne(normalCase);

                //LO = 0x80000000;
                //HI = 0;
                asm.mov(__dword_ptr[rbx + LO_Offset], 0x80000000);
                asm.mov(__dword_ptr[rbx + HI_Offset], 0);
                asm.jmp(end);

                asm.Label(ref normalCase);
                asm.cdq();
                asm.idiv(ecx);                               //Divide  edx:eax / ecx

            } else {

                //Only one check, if denominator == 0
                //Check the inverse
                asm.mov(esi, 0);
                asm.cmp(ecx, esi);
                asm.jne(normalCase);

                //LO = 0xffffffff;
                //HI = numerator;
                asm.mov(__dword_ptr[rbx + LO_Offset], 0xffffffff);
                asm.mov(__dword_ptr[rbx + HI_Offset], eax);
                asm.jmp(end);

                asm.Label(ref normalCase);
                asm.mov(edx, 0);
                asm.div(ecx);                               //Divide  edx:eax / ecx
            }

            //Quotient in eax and remainder in edx
            asm.mov(__dword_ptr[rbx + LO_Offset], eax);  //LO = numerator / denominator;
            asm.mov(__dword_ptr[rbx + HI_Offset], edx);  //HI = numerator % denominator;

            asm.Label(ref end);
        }

        public static void EmitMULT(int rs, int rt, bool signed, Assembler asm) {
            EmitRegisterRead(asm, eax, rs);
            EmitRegisterRead(asm, ecx, rt);

            if (signed) {
                asm.imul(ecx);   // edx:eax = signed eax * signed ecx

            } else {
                asm.mul(ecx);   //edx:eax = eax * ecx
            }

            asm.mov(__dword_ptr[rbx + LO_Offset], eax);
            asm.mov(__dword_ptr[rbx + HI_Offset], edx);
        }

        public static void EmitLUI(int rt, uint imm, Assembler asm) {
            EmitRegisterWrite(asm, rt, imm << 16);
        }

        public static void EmitMTC0(int rt, int rd, Assembler asm) {
            if (rd == 12) {
                //cpu.Cop0.SR = cpu.GPR[instruction.Get_rt()]; -> That's what we care about for now
                EmitRegisterRead(asm, ecx, rt);
                asm.mov(__dword_ptr[rbx + COP0_SR_Offset], ecx);
            }
        }

        public static void EmitMFC0(int rt, int rd, Assembler asm) {
            int offset = 0;

            switch (rd) {
                case 12: offset = COP0_SR_Offset; break;
                case 13: offset = COP0_Cause_Offset; break;
                case 14: offset = COP0_EPC_Offset; break;
                case 15: asm.mov(ecx, 0x00000002); break; //COP0 R15 (PRID)
                default: rt = 0; Console.WriteLine("Unhandled cop0 Register Read: " + rd); break;
            }

            if (rd != 15) {
                asm.mov(ecx, __dword_ptr[rbx + offset]);
            }

            //MFC has load delay!
            EmitRegisterWrite(asm, rt, ecx, true);
        }

        public static void EmitRFE(Assembler asm) {
            /* 
            uint temp = cpu.Cop0.SR;
            cpu.Cop0.SR = (uint)(cpu.Cop0.SR & (~0xF));
            cpu.Cop0.SR |= ((temp >> 2) & 0xF);
            */
            asm.mov(ecx, __dword_ptr[rbx + COP0_SR_Offset]);    //SR
            asm.mov(edx, ecx);                                  //Copy of SR (temp)
            asm.and(ecx, ~0xF);                                 //SR = SR & (~0xF)
            asm.shr(edx, 2);                                    //temp = temp >> 2
            asm.and(edx, 0xF);                                  //temp = temp & 0xF
            asm.or(ecx, edx);                                   //SR = SR | temp
            asm.mov(__dword_ptr[rbx + COP0_SR_Offset], ecx);    //Write back
        }

        public static void EmitMF(int rd, bool isHI, Assembler asm) {
            int offset = isHI ? HI_Offset : LO_Offset;
            asm.mov(ecx, __dword_ptr[rbx + offset]);
            EmitRegisterWrite(asm, rd, ecx, false);
        }

        public static void EmitMT(int rs, bool isHI, Assembler asm) {
            int offset = isHI ? HI_Offset : LO_Offset;
            EmitRegisterRead(asm, ecx, rs);
            asm.mov(__dword_ptr[rbx + offset], ecx);
        }

        public static void EmitCOP2Command(uint instruction, Assembler asm) {
            //Call GTE.execute(instruction);
            asm.mov(r15, GetGTExecuteAddress());    //Load function pointer
            asm.mov(ecx, instruction);              //Parameter in ecx
            asm.call(r15);                          //Call GTE Execute
        }

        public static void EmitMFC2_CFC2(int rt, int rd, Assembler asm) {
            //Call GTE.read(rd);
            asm.mov(r15, GetGTEReadAddress());      //Load function pointer
            asm.mov(ecx, rd);                       //Parameter in rcx
            asm.call(r15);                          //Call GTE Read, result is written to eax

            //There is a delay slot
            EmitRegisterWrite(asm, rt, eax, true);
        }

        public static void EmitMTC2_CTC2(int rt, int rd, Assembler asm) {
            //Call GTE.write(rd, value);
            asm.mov(ecx, rd);                       //Parameter in ecx
            EmitRegisterRead(asm, edx, rt);         //Parameter in edx
            asm.mov(r15, GetGTEWriteAddress());     //Load function pointer
            asm.call(r15);                          //Call GTE Write
        }

        public static void EmitLWC2(int rs, int rt, uint imm, Assembler asm) {
            //Call bus.loadword(address);
            EmitCalculateAddress(asm, ecx, rs, imm);    //Address -> ecx
            asm.mov(r15, GetBUSReadWordAddress());     //Load function pointer
            asm.call(r15);                             //Call BUS ReadWord, result is written to eax

            //Call cpu.GTE.write(rd, value);
            asm.mov(ecx, rt);                           //Move rt to ecx (parameter 1)
            asm.mov(edx, eax);                          //Move loaded value to edx (parameter 2)
            asm.mov(r15, GetGTEWriteAddress());         //Load function pointer
            asm.call(r15);                              //Call GTE write
        }

        public static void EmitSWC2(int rs, int rt, uint imm, Assembler asm) {
            //Call cpu.GTE.read(rt);
            asm.mov(ecx, rt);                           //Parameter in rcx
            asm.mov(r15, GetGTEReadAddress());          //Load function pointer
            asm.call(r15);                              //Call GTE Read, result is written to eax

            //Write the value to the memory
            EmitCalculateAddress(asm, ecx, rs, imm);    //Address -> ecx
            asm.mov(edx, eax);                          //Move eax to edx (parameter 2)
            asm.mov(r15, GetBUSWriteWordAddress());     //Load function pointer
            asm.call(r15);                              //Call bus writeword
        }

        public static void EmitMemoryLoad(int rs, int rt, uint imm, int size, bool signed, Assembler asm) {
            Label end = asm.CreateLabel();

            //Check cache isolation!
            EmitCheckCacheIsolation(asm);   //result in carry flag
            asm.jc(end);                   //if not zero we exit

            EmitCalculateAddress(asm, ecx, rs, imm);    //Address -> ecx

            switch (size) {
                case MemoryReadWriteSize.BYTE:
                    asm.mov(r15, GetBUSReadByteAddress());     //Load function pointer
                    asm.call(r15);                             //Result in eax
                    if (signed) {
                        asm.movsx(eax, al);             //Sign-extend 8-bit al to 32-bit eax
                    }
                    break;

                case MemoryReadWriteSize.HALF:
                    asm.mov(r15, GetBUSReadHalfAddress());     //Load function pointer
                    asm.call(r15);                             //Result in eax
                    if (signed) {
                        asm.movsx(eax, ax);             //Sign-extend 16-bit ax to 32-bit eax
                    }
                    break;

                case MemoryReadWriteSize.WORD:
                    asm.mov(r15, GetBUSReadWordAddress());     //Load function pointer
                    asm.call(r15);                             //Result in eax, there is not signed 32-bits version
                    break;
            }

            //There is a delay slot
            EmitRegisterWrite(asm, rt, eax, true);
            asm.Label(ref end);
        }

        public static void EmitMemoryStore(int rs, int rt, uint imm, int size, Assembler asm) {
            Label end = asm.CreateLabel();

            //Check cache isolation!
            EmitCheckCacheIsolation(asm);   //result in carry flag
            asm.jc(end);                   //if not zero we exit

            EmitCalculateAddress(asm, ecx, rs, imm);    //Address -> ecx 

            //Load GPR[rt]
            EmitRegisterRead(asm, edx, rt);            //Value -> edx


            switch (size) {
                case MemoryReadWriteSize.BYTE:
                    asm.and(edx, 0xFF);                         //Mask to one byte
                    asm.mov(r15, GetBUSWriteByteAddress());     //Load function pointer
                    break;

                case MemoryReadWriteSize.HALF:
                    asm.and(edx, 0xFFFF);                       //Mask to 2 bytes
                    asm.mov(r15, GetBUSWriteHalfAddress());     //Load function pointer
                    break;

                case MemoryReadWriteSize.WORD:                  //No mask needed
                    asm.mov(r15, GetBUSWriteWordAddress());     //Load function pointer
                    break;
            }

            asm.call(r15);                                      //Call BUS.WriteXX
            asm.Label(ref end);
        }

        public static void EmitLWL(int rs, int rt, uint imm, Assembler asm) {
            Label loadPos = asm.CreateLabel();
            Label finalStep = asm.CreateLabel();
            Label end = asm.CreateLabel();

            Label case1 = asm.CreateLabel();
            Label case2 = asm.CreateLabel();
            Label case3 = asm.CreateLabel();

            //Check cache isolation!
            EmitCheckCacheIsolation(asm);   //result in carry flag
            asm.jc(end);                   //if not zero we exit

            EmitCalculateAddress(asm, ecx, rs, imm);    //Address -> ecx
            asm.mov(r8d, ecx);                          //Copy of address -> r8d

            EmitRegisterRead(asm, edx, rt);            //current_value -> edx

            if (EnableLoadDelaySlot || IsFirstInstruction) {
                //Bypass load delay if rt == ReadyRegisterLoad.RegisterNumber
                asm.mov(esi, rt);
                asm.mov(edi, __dword_ptr[rbx + ReadyRegisterLoad_number]);
                asm.cmp(esi, edi);
                asm.jne(loadPos);                               //Skip if they are not equal
                asm.mov(edx, __dword_ptr[rbx + ReadyRegisterLoad_value]);                 //Overwrite current_value (edx)
            }


            asm.Label(ref loadPos);
            asm.and(ecx, ~3);                       //ecx &= ~3

            //Copy edx and r8d to callee-saved registers
            asm.mov(r13d, edx);
            asm.mov(r14d, r8d);

            asm.mov(r15, GetBUSReadWordAddress());  //Load function pointer
            asm.call(r15);                          //Load word from Address & ~3, result is in eax

            asm.mov(edx, r13d);
            asm.mov(r8d, r14d);

            asm.and(r8d, 3);                        //pos in r8d


            //edx -> current_value
            //eax -> word

            //Switch:
            //Case 0: finalValue = current_value & 0x00ffffff | word << 24; break;
            asm.cmp(r8d, 0);
            asm.jne(case1);

            asm.and(edx, 0x00ffffff);
            asm.shl(eax, 24);
            asm.or(edx, eax);

            asm.jmp(finalStep);

            //case 1: finalValue = current_value & 0x0000ffff | word << 16; break;
            asm.Label(ref case1);
            asm.cmp(r8d, 1);
            asm.jne(case2);

            asm.and(edx, 0x0000ffff);
            asm.shl(eax, 16);
            asm.or(edx, eax);

            asm.jmp(finalStep);

            //case 2: finalValue = current_value & 0x000000ff | word << 8; break;
            asm.Label(ref case2);
            asm.cmp(r8d, 2);
            asm.jne(case3);

            asm.and(edx, 0x000000ff);
            asm.shl(eax, 8);
            asm.or(edx, eax);

            asm.jmp(finalStep);

            //case 3:  finalValue = current_value & 0x00000000 | word << 0; break;
            asm.Label(ref case3);

            asm.and(edx, 0x00000000);
            asm.shl(eax, 0);
            asm.or(edx, eax);



            asm.Label(ref finalStep);

            //Write to finalValue (edx) to GPR[rt]
            EmitRegisterWrite(asm, rt, edx, true);  //There is a load delay

            asm.Label(ref end);
        }

        public static void EmitLWR(int rs, int rt, uint imm, Assembler asm) {
            Label loadPos = asm.CreateLabel();
            Label finalStep = asm.CreateLabel();
            Label end = asm.CreateLabel();

            Label case1 = asm.CreateLabel();
            Label case2 = asm.CreateLabel();
            Label case3 = asm.CreateLabel();

            //Check cache isolation!
            EmitCheckCacheIsolation(asm);   //result in carry flag
            asm.jc(end);                   //if not zero we exit

            EmitCalculateAddress(asm, ecx, rs, imm);    //Address -> ecx

            asm.mov(r8d, ecx);                          //Copy of address -> r8d

            EmitRegisterRead(asm, edx, rt);            //current_value -> edx

            if (EnableLoadDelaySlot || IsFirstInstruction) {
                //Bypass load delay if rt == ReadyRegisterLoad.RegisterNumber
                asm.mov(esi, rt);
                asm.mov(edi, __dword_ptr[rbx + ReadyRegisterLoad_number]);
                asm.cmp(esi, edi);
                asm.jne(loadPos);                               //Skip if they are not equal
                asm.mov(edx, __dword_ptr[rbx + ReadyRegisterLoad_value]);                 //Overwrite current_value (edx)
            }


            asm.Label(ref loadPos);
            asm.and(ecx, ~3);                       //ecx &= ~3

            asm.mov(r13d, edx);
            asm.mov(r14d, r8d);

            asm.mov(r15, GetBUSReadWordAddress());  //Load function pointer
            asm.call(r15);                          //Load word from Address & ~3, result is in eax

            asm.mov(edx, r13d);
            asm.mov(r8d, r14d);

            asm.and(r8d, 3);                        //pos in r8d

            //edx -> current_value
            //eax -> word

            //Switch:
            //Case 0: finalValue = current_value & 0x00000000 | word >> 0;
            asm.cmp(r8d, 0);
            asm.jne(case1);

            asm.and(edx, 0x00000000);
            asm.shr(eax, 0);
            asm.or(edx, eax);

            asm.jmp(finalStep);

            //case 1: finalValue = current_value & 0xff000000 | word >> 8;
            asm.Label(ref case1);
            asm.cmp(r8d, 1);
            asm.jne(case2);

            asm.and(edx, 0xff000000);
            asm.shr(eax, 8);
            asm.or(edx, eax);

            asm.jmp(finalStep);

            //case 2: finalValue = current_value & 0xffff0000 | word >> 16;
            asm.Label(ref case2);
            asm.cmp(r8d, 2);
            asm.jne(case3);

            asm.and(edx, 0xffff0000);
            asm.shr(eax, 16);
            asm.or(edx, eax);

            asm.jmp(finalStep);

            //case 3:  finalValue = current_value & 0xffffff00 | word >> 24; 
            asm.Label(ref case3);

            asm.and(edx, 0xffffff00);
            asm.shr(eax, 24);
            asm.or(edx, eax);


            asm.Label(ref finalStep);

            //Write to finalValue (edx) to GPR[rt]
            EmitRegisterWrite(asm, rt, edx, true);  //There is a load delay

            asm.Label(ref end);
        }

        public static void EmitSWL(int rs, int rt, uint imm, Assembler asm) {
            Label finalStep = asm.CreateLabel();
            Label end = asm.CreateLabel();

            Label case1 = asm.CreateLabel();
            Label case2 = asm.CreateLabel();
            Label case3 = asm.CreateLabel();

            //Check cache isolation!
            EmitCheckCacheIsolation(asm);   //result in carry flag
            asm.jc(end);                   //if not zero we exit

            EmitCalculateAddress(asm, ecx, rs, imm);    //final_address -> ecx
            asm.mov(r8d, ecx);                          //Copy of final_address -> r8d
            asm.mov(esi, ecx);                          //Another copy of final_address -> esi

            EmitRegisterRead(asm, edx, rt);             //value -> edx

            asm.and(ecx, ~3);                           //final_address &= ~3

            //Copy esi and edx and r8d to callee-saved registers
            asm.mov(r12d, esi);
            asm.mov(r13d, edx);
            asm.mov(r14d, r8d);

            asm.mov(r15, GetBUSReadWordAddress());  //Load function pointer
            asm.call(r15);                          //current_value -> eax

            asm.mov(esi, r12d);
            asm.mov(edx, r13d);
            asm.mov(r8d, r14d);

            asm.and(r8d, 3);                            //pos -> r8d


            //edx -> value
            //eax -> current_value

            //Switch:
            //case 0: finalValue = current_value & 0xffffff00 | value >> 24;
            asm.cmp(r8d, 0);
            asm.jne(case1);

            asm.and(eax, 0xffffff00);
            asm.shr(edx, 24);
            asm.or(edx, eax);

            asm.jmp(finalStep);

            //case 1: finalValue = current_value & 0xffff0000 | value >> 16;
            asm.Label(ref case1);
            asm.cmp(r8d, 1);
            asm.jne(case2);

            asm.and(eax, 0xffff0000);
            asm.shr(edx, 16);
            asm.or(edx, eax);

            asm.jmp(finalStep);

            //case 2: finalValue = current_value & 0xff000000 | value >> 8;
            asm.Label(ref case2);
            asm.cmp(r8d, 2);
            asm.jne(case3);

            asm.and(eax, 0xff000000);
            asm.shr(edx, 8);
            asm.or(edx, eax);

            asm.jmp(finalStep);

            //case 3: finalValue = current_value & 0x00000000 | value >> 0;
            asm.Label(ref case3);

            asm.and(eax, 0x00000000);
            asm.shr(edx, 0);
            asm.or(edx, eax);


            asm.Label(ref finalStep);

            //final_address & ~3 -> ecx, and final value is already in edx, shadow space is already added
            asm.mov(ecx, esi);
            asm.and(ecx, ~3);
            asm.mov(r15, GetBUSWriteWordAddress());     //Load function pointer
            asm.call(r15);

            asm.Label(ref end);
        }

        public static void EmitSWR(int rs, int rt, uint imm, Assembler asm) {
            Label finalStep = asm.CreateLabel();
            Label end = asm.CreateLabel();

            Label case1 = asm.CreateLabel();
            Label case2 = asm.CreateLabel();
            Label case3 = asm.CreateLabel();

            //Check cache isolation!
            EmitCheckCacheIsolation(asm);   //result in carry flag
            asm.jc(end);                   //if not zero we exit

            EmitCalculateAddress(asm, ecx, rs, imm);    //final_address -> ecx
            asm.mov(r8d, ecx);                          //Copy of final_address -> r8d
            asm.mov(esi, ecx);                          //Another copy of final_address -> esi

            EmitRegisterRead(asm, edx, rt);             //value -> edx

            asm.and(ecx, ~3);                           //final_address &= ~3

            //Copy esi and edx and r8d to callee-saved registers
            asm.mov(r12d, esi);
            asm.mov(r13d, edx);
            asm.mov(r14d, r8d);

            asm.mov(r15, GetBUSReadWordAddress());  //Load function pointer
            asm.call(r15);                          //current_value -> eax

            asm.mov(esi, r12d);
            asm.mov(edx, r13d);
            asm.mov(r8d, r14d);


            asm.and(r8d, 3);                            //pos -> r8d


            //edx -> value
            //eax -> current_value

            //Switch:
            //case 0: finalValue = current_value & 0x00000000 | value << 0;
            asm.cmp(r8d, 0);
            asm.jne(case1);

            asm.and(eax, 0x00000000);
            asm.shl(edx, 0);
            asm.or(edx, eax);

            asm.jmp(finalStep);

            //case 1: finalValue = current_value & 0x000000ff | value << 8;
            asm.Label(ref case1);
            asm.cmp(r8d, 1);
            asm.jne(case2);

            asm.and(eax, 0x000000ff);
            asm.shl(edx, 8);
            asm.or(edx, eax);

            asm.jmp(finalStep);

            //case 2: finalValue = current_value & 0x0000ffff | value << 16;
            asm.Label(ref case2);
            asm.cmp(r8d, 2);
            asm.jne(case3);

            asm.and(eax, 0x0000ffff);
            asm.shl(edx, 16);
            asm.or(edx, eax);

            asm.jmp(finalStep);

            //case 3: finalValue = current_value & 0x00ffffff | value << 24;
            asm.Label(ref case3);

            asm.and(eax, 0x00ffffff);
            asm.shl(edx, 24);
            asm.or(edx, eax);


            asm.Label(ref finalStep);

            //final_address & ~3 -> ecx, and final value is already in edx, shadow space is already added
            asm.mov(ecx, esi);
            asm.and(ecx, ~3);
            asm.mov(r15, GetBUSWriteWordAddress());     //Load function pointer
            asm.call(r15);

            asm.Label(ref end);
        }

        private static void EmitCalculateAddress(Assembler asm, AssemblerRegister32 dst, int sourceReg, uint imm) {
            EmitRegisterRead(asm, dst, sourceReg);  //Read GPR
            asm.add(dst, imm);                      //Add to it imm
        }

        public static void EmitRegisterTransfare(Assembler asm) {
            /*
             if (cpu.ReadyRegisterLoad.RegisterNumber != cpu.DelayedRegisterLoad.RegisterNumber) {
                cpu.GPR[cpu.ReadyRegisterLoad.RegisterNumber] = cpu.ReadyRegisterLoad.Value;
            }
            */


            Label skip = asm.CreateLabel();

            asm.mov(ecx, __dword_ptr[rbx + ReadyRegisterLoad_number]);
            asm.cmp(ecx, __dword_ptr[rbx + DelayedRegisterLoad_number]);
            asm.je(skip);

            asm.mov(eax, __dword_ptr[rbx + ReadyRegisterLoad_value]);           //eax = ReadyRegisterLoad.Value
            asm.shl(ecx, 2);                                                    //ecx = ecx * 4 (offset)


            //Write ReadyRegisterLoad.Value to base address + offset of GPR + offset of register
            //If offset is 0 we don't need to add anything, which it is currently
            if (GPR_Offset > 0) {
                asm.mov(__dword_ptr[rbx + rcx + GPR_Offset], eax);
            } else {
                asm.mov(__dword_ptr[rbx + rcx], eax);
            }

            asm.Label(ref skip);

            /*
             cpu.ReadyRegisterLoad.Value = cpu.DelayedRegisterLoad.Value;
             cpu.ReadyRegisterLoad.RegisterNumber = cpu.DelayedRegisterLoad.RegisterNumber;
            */

            //Since we know the layout, we can do a single 64-bit move that moves both Value and Number
            asm.mov(rax, __qword_ptr[rbx + DelayedRegisterLoad_number]);
            asm.mov(__qword_ptr[rbx + ReadyRegisterLoad_number], rax);

            /*
             //Last step is direct register write, so it can overwrite any memory load on the same register
             cpu.GPR[cpu.DirectWrite.RegisterNumber] = cpu.DirectWrite.Value;
            */

            asm.mov(eax, __dword_ptr[rbx + DirectWrite_value]);                 //eax = DirectWrite.Value
            asm.mov(ecx, __dword_ptr[rbx + DirectWrite_number]);                //ecx = DirectWrite.Number
            asm.shl(ecx, 2);                                                    //ecx = ecx * 4 (offset)

            //Note: we need 64-bit regs for the pointer + offset calculation
            //Write ReadyRegisterLoad.Value to base address + offset

            //If offset is 0 we don't need to add anything, which it is currently
            if (GPR_Offset > 0) {
                asm.mov(__dword_ptr[rbx + rcx + GPR_Offset], eax);
            } else {
                asm.mov(__dword_ptr[rbx + rcx], eax);
            }

            /*
            cpu.DelayedRegisterLoad.Value = 0;
            cpu.DelayedRegisterLoad.RegisterNumber = 0;
            cpu.DirectWrite.RegisterNumber = 0;
            cpu.DirectWrite.Value = 0;
            cpu.GPR[0] = 0;
            */

            asm.xor(rax, rax);

            //Since we know the layout, we can clear each 2 integers with a single 64-bit move
            asm.mov(__qword_ptr[rbx + DelayedRegisterLoad_number], rax);
            asm.mov(__qword_ptr[rbx + DirectWrite_number], rax);

            //If offset is 0 we don't need to add anything, which it is currently

            if (GPR_Offset > 0) {
                asm.mov(__dword_ptr[rbx + GPR_Offset], eax);
            } else {
                asm.mov(__dword_ptr[rbx], eax);
            }
        }

        public static void EmitBranchDelayHandler(Assembler asm) {
            /*
            cpu.DelaySlot = cpu.Branch;   //Branch delay 
            cpu.Branch = false;
            cpu.PC = cpu.Next_PC;
            cpu.Next_PC = cpu.Next_PC + 4;
            */

            asm.mov(eax, __dword_ptr[rbx + BranchFlagOffset]);
            asm.mov(__dword_ptr[rbx + DelaySlotOffset], eax);

            asm.mov(__dword_ptr[rbx + BranchFlagOffset], 0);

            asm.mov(eax, __dword_ptr[rbx + NextPCOffset]);
            asm.mov(__dword_ptr[rbx + PCOffset], eax);

            asm.add(__dword_ptr[rbx + NextPCOffset], 4);
        }

        public static void EmitSavePC(Assembler asm) {
            //Current_PC = PC;
            asm.mov(eax, __dword_ptr[rbx + PCOffset]);
            asm.mov(__dword_ptr[rbx + CurrentPCOffset], eax);
        }

        public static void EmitUpdateCurrentCycle(Assembler asm, int addValue) {
            asm.add(__qword_ptr[rbx + CurrentCycle_Offset], addValue);
        }

        public static void EmitUpdatePC(Assembler asm, int numberOfInstructions) {
            int offset = numberOfInstructions * 4;
            //asm.add(__dword_ptr[rbx + CurrentPCOffset], offset - 4);
            asm.add(__dword_ptr[rbx + PCOffset], offset);
            asm.add(__dword_ptr[rbx + NextPCOffset], offset + 4);
        }

        public static void EmitBlockEntry(Assembler asm) {
            EmitSaveNonVolatileRegisters(asm);          //Store callee-saved regs on stack 
            asm.mov(rbp, rsp);                          //Copy stack pointer
            asm.sub(rsp, 40);                           //Prepare shadow space
            asm.mov(rbx, GetCPUStructAddress());        //Pre load base CPU pointer
        }

        public static void TerminateBlock(Assembler asm, ref Label endOfBlock) {
            asm.add(rsp, 40);                               //Undo shadow space
            EmitRestoreNonVolatileRegisters(asm);           //Restore callee-saved regs
            asm.ret();                                      //Return
            asm.Label(ref endOfBlock);
            asm.nop();                      //This nop will not be included, but we need an instruction to define a label
        }

        public static void EmitBranch(Assembler asm, uint offset) {
            asm.add(__dword_ptr[rbx + NextPCOffset], (offset << 2) - 4);
            asm.mov(__dword_ptr[rbx + BranchFlagOffset], 1);
        }

        public static void EmitTTY(Assembler asm, uint address) {
            if (address == 0xA0) {
                asm.mov(r15, GetTTYA0Handler());

            } else if(address == 0xB0){
                asm.mov(r15, GetTTYB0Handler());

            } else {
                //Unreachable
                throw new UnreachableException();
            }

            asm.call(r15);
        }

        public static void EmitSaveNonVolatileRegisters(Assembler asm) {
            asm.push(rbx);
            asm.push(rdi);
            asm.push(rsi);
            asm.push(rbp);
            asm.push(r12);
            asm.push(r13);
            asm.push(r14);
            asm.push(r15);
        }

        public static void EmitRestoreNonVolatileRegisters(Assembler asm) {
            asm.pop(r15);
            asm.pop(r14);
            asm.pop(r13);
            asm.pop(r12);
            asm.pop(rbp);
            asm.pop(rsi);
            asm.pop(rdi);
            asm.pop(rbx);
        }

        public static Span<byte> EmitStubBlock() {
            //Stub code in all non compiled blocks
            //Except rsp, only use volatile registers, otherwise we need to push/pop
            Assembler asm = new Assembler(64);
            Label endOfFunction = asm.CreateLabel();

            asm.sub(rsp, 40);       //Prepare shadow space

            //Call the handler which calls recompile function
            asm.mov(rcx, GetStubBlockHandlerAddress());
            asm.call(rcx);

            //Jump to returned block address in rax
            asm.call(rax);

            asm.add(rsp, 40);       //Undo shadow space
            asm.ret();

            asm.Label(ref endOfFunction);
            asm.nop();

            MemoryStream stream = new MemoryStream();
            AssemblerResult result = asm.Assemble(new StreamCodeWriter(stream), 0, BlockEncoderOptions.ReturnNewInstructionOffsets);
            int endOfBlock = (int)result.GetLabelRIP(endOfFunction);
            Span<byte> emittedCode = new Span<byte>(stream.GetBuffer()).Slice(0, endOfBlock);
            return emittedCode;
        }
    }

    public unsafe class x64CacheBlock {
        public uint Address;
        public uint TotalCycles;
        public int SizeOfAllocatedBytes;
        public delegate* unmanaged[Stdcall]<void> FunctionPointer;
    }
}
