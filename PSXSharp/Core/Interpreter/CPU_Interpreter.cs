using PSXSharp.Core.Common;
using PSXSharp.Core.x64_Recompiler;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using static PSXSharp.Core.CPU;

namespace PSXSharp.Core.Interpreter {
    public unsafe class CPU_Interpreter : CPU {

        //BUS connects CPU to other peripherals
        public BUS BUS;

        //32-bit program counters
        public uint PC;
        public uint Next_PC;
        public uint Current_PC;

        //General Purpose Registers
        public uint[] GPR;

        //COP0
        public struct COP0 {
            public uint SR;         //R12 , the status register 
            public uint Cause;      //R13 , the cause register 
            public uint EPC;        //R14 , Return Address from Trap
        }

        public COP0 Cop0;

        public uint HI;           //Remainder of devision
        public uint LO;           //Quotient of devision

        //Flags to emulate branch delay 
        public bool Branch;
        private bool DelaySlot;

        //This is needed because writes to memory are ignored (well, not really) When cache is isolated
        public bool IscIsolateCache => (Cop0.SR & 0x10000) != 0;

        //Geometry Transformation Engine - Coprocessor 2
        public GTE GTE = new GTE();

        //Counting how many cycles to clock the other peripherals
        public static int Cycles = 0;

        private byte[] EXE;
        private bool IsLoadingEXE;
        private string? EXEPath;

        bool FastBoot = false;                  //Skips the boot animation 
        public bool IsStopped = false;

        const uint CYCLES_PER_SECOND = 33868800;
        const uint CYCLES_PER_FRAME = CYCLES_PER_SECOND / 60;
        const int CYCLES_PER_SPU_SAMPLE = 0x300;

        ulong CurrentCycle = 0;
        double CyclesDone = 0;

        List<byte> Chars = new List<byte>();    //Temporarily stores characters 
      
        private static readonly delegate* <CPU_Interpreter, Instruction, void>[] MainLookUpTable = [
                &special,   &bxx,       &jump,      &jal,       &beq,        &bne,       &blez,      &bgtz,
                &addi,      &addiu,     &slti,      &sltiu,     &andi,       &ori,       &xori,      &lui,
                &cop0,      &cop1,      &cop2,      &cop3,      &illegal,    &illegal,   &illegal,   &illegal,
                &illegal,   &illegal,   &illegal,   &illegal,   &illegal,    &illegal,   &illegal,   &illegal,
                &lb,        &lh,        &lwl,       &lw,        &lbu,        &lhu,       &lwr,       &illegal,
                &sb,        &sh,        &swl,       &sw,        &illegal,    &illegal,   &swr,       &illegal,
                &lwc0,      &lwc1,      &lwc2,      &lwc3,      &illegal,    &illegal,   &illegal,   &illegal,
                &swc0,      &swc1,      &swc2,      &swc3,      &illegal,    &illegal,   &illegal,   &illegal
        ];

        private static readonly delegate* <CPU_Interpreter, Instruction, void>[] SpecialLookUpTable = [
                &sll,       &illegal,   &srl,       &sra,       &sllv,      &illegal,   &srlv,      &srav,
                &jr,        &jalr,      &illegal,   &illegal,   &syscall,   &break_,    &illegal,   &illegal,
                &mfhi,      &mthi,      &mflo,      &mtlo,      &illegal,   &illegal,   &illegal,   &illegal,
                &mult,      &multu,     &div,       &divu,      &illegal,   &illegal,   &illegal,   &illegal,
                &add,       &addu,      &sub,       &subu,      &and,       &or,        &xor,       &nor,
                &illegal,   &illegal,   &slt,       &sltu,      &illegal,   &illegal,   &illegal,   &illegal,
                &illegal,   &illegal,   &illegal,   &illegal,   &illegal,   &illegal,   &illegal,   &illegal,
                &illegal,   &illegal,   &illegal,   &illegal,   &illegal,   &illegal,   &illegal,   &illegal
        ];

     
        public RegisterLoad ReadyRegisterLoad;
        public RegisterLoad DelayedRegisterLoad;
        public RegisterLoad DirectWrite;       //Not memory access, will overwrite memory loads

        Instruction CurrentInstruction = new Instruction();
        bool IsReadingFromBIOS => BUS.BIOS.range.Contains(BUS.Mask(PC));

        public CPU_Interpreter(bool isEXE, string? EXEPath, BUS bus) {
            PC = 0xbfc00000;                   //BIOS initial PC       
            Next_PC = PC + 4;
            BUS = bus;
            GPR = new uint[32];
            GPR[0] = 0;
            Cop0.SR = 0;
            DirectWrite.RegisterNumber = 0;    //Stupid load delay slot
            DirectWrite.Value = 0;
            ReadyRegisterLoad.RegisterNumber = 0;
            ReadyRegisterLoad.Value = 0;
            DelayedRegisterLoad.RegisterNumber = 0;
            DelayedRegisterLoad.Value = 0; 
            HI = 0xdeadbeef;
            LO = 0xdeadbeef;
            Branch = false;
            DelaySlot = false;
            IsLoadingEXE = isEXE;
            this.EXEPath = EXEPath;

            //Scheduler is static, make sure to clear it when resetting
            Scheduler.FlushAllEvents();

            //Schedule 1 initial SPU event
            Scheduler.ScheduleInitialEvent(CYCLES_PER_SPU_SAMPLE, BUS.SPU.SPUCallback, Event.SPU);

            //Schedule 1 initial vblank event
            Scheduler.ScheduleInitialEvent((int)CYCLES_PER_FRAME, BUS.GPU.VblankEventCallback, Event.Vblank);
        }
       
        public void emu_cycle() {
            Current_PC = PC;   //Save current pc In case of an exception
            Intercept(PC);     //For TTY

             if (FastBoot) {
                 if (PC == 0x80030000) {
                    PC = GPR[(int)CPU.Register.ra];
                    Next_PC = PC + 4;
                    FastBoot = false;
                    ReadyRegisterLoad.Value = 0;
                    ReadyRegisterLoad.RegisterNumber = 0;
                    DelayedRegisterLoad = ReadyRegisterLoad;
                    return;
                 }
             }
    
            //PC must be 32 bit aligned, can be ignored?
            if ((Current_PC & 0x3) != 0) {
                Exception(this, (uint)CPU.Exceptions.LoadAddressError);
                return;
            }

            CurrentInstruction.FullValue = BUS.LoadWord(PC);    

            DelaySlot = Branch;   //Branch delay 
            Branch = false;

            PC = Next_PC;
            Next_PC = Next_PC + 4;

            /*if (BUS.debug) {
                Console.WriteLine("[" + Current_PC.ToString("x").PadLeft(8, '0') + "]" + " --- " + CurrentInstruction.Getfull().ToString("x").PadLeft(8,'0'));
            }*/
    
            ExecuteInstruction(CurrentInstruction);
            RegisterTransfer(this);
            CurrentCycle += (ulong)(IsReadingFromBIOS ? 22 : 2); ;
        }

        private void IRQCheck() {
            if (IRQ_CONTROL.isRequestingIRQ()) {  //Interrupt check 
                Cop0.Cause |= 1 << 10;

                //Skip IRQs if the current instruction is a GTE instruction to avoid the BIOS skipping it
                if ((Cop0.SR & 1) != 0 && (Cop0.SR >> 10 & 1) != 0 && !InstructionIsGTE(this)) {
                    Exception(this, (uint)CPU.Exceptions.IRQ);
                    return;
                }
            }
        }

        private bool InstructionIsGTE(CPU_Interpreter cpu) {          
            return (cpu.CurrentInstruction.FullValue & 0xFE000000) == 0x4A000000;
        }

        private void ExecuteInstruction(Instruction instruction) {
            MainLookUpTable[instruction.GetOpcode()](this, instruction);
        }

        private static void special(CPU_Interpreter cpu, Instruction instruction) {
            SpecialLookUpTable[instruction.Get_Subfunction()](cpu, instruction);
        }

        private void RegisterTransfer(CPU_Interpreter cpu){    //Handle register transfers and delay slot
            if (cpu.ReadyRegisterLoad.RegisterNumber != cpu.DelayedRegisterLoad.RegisterNumber) {
                cpu.GPR[cpu.ReadyRegisterLoad.RegisterNumber] = cpu.ReadyRegisterLoad.Value;
            }
            cpu.ReadyRegisterLoad.Value = cpu.DelayedRegisterLoad.Value;
            cpu.ReadyRegisterLoad.RegisterNumber = cpu.DelayedRegisterLoad.RegisterNumber;

            cpu.DelayedRegisterLoad.Value = 0;
            cpu.DelayedRegisterLoad.RegisterNumber = 0;

            //Last step is direct register write, so it can overwrite any memory load on the same register
            cpu.GPR[cpu.DirectWrite.RegisterNumber] = cpu.DirectWrite.Value;
            cpu.DirectWrite.RegisterNumber = 0;
            cpu.DirectWrite.Value = 0;
            cpu.GPR[0] = 0;
        }

        private void Intercept(uint pc) {

            switch (pc) {
               case 0x80030000: if (IsLoadingEXE) { loadTestRom(EXEPath); IsLoadingEXE = false; } break;

                case 0xA0:      //Intercepting prints to the TTY Console and printing it in console 
                    char character;

                    switch (GPR[9]) {

                        case 0x3C:                       //putchar function (Prints the char in $a0)
                            character = (char)GPR[4];
                            Console.Write(character);
                            break;

                        case 0x3E:                        //puts function, similar to printf but differ in dealing with 0 character
                            uint address = GPR[4];       //address of the string is in $a0
                            if (address == 0) {
                                Console.Write("\\<NULL>");
                            }
                            else {
                                while (BUS.LoadByte(address) != 0) {
                                    character = (char)BUS.LoadByte(address);
                                    Console.Write(character);
                                    address++;
                                }

                            }

                            break;

                        default:
                            if (BUS.debug) {
                                Console.WriteLine("Function A: " + GPR[9].ToString("x"));
                            }

                            break;


                    }
                    break;

                case 0xB0:
                    switch (GPR[9]) {
                        case 0x3D:                       //putchar function (Prints the char in $a0)
                            character = (char)GPR[4];
                            if (char.IsAscii(character)) {
                                if (Chars.Count > 0) {
                                    string unicoded = Encoding.UTF8.GetString(Chars.ToArray());
                                    Console.Write(unicoded);
                                    Chars.Clear();

                                }
                                Console.Write(character);
                            } else {
                                Chars.Add((byte)GPR[4]);
                            }
                            break;

                        case 0x3F:                          //puts function, similar to printf but differ in dealing with 0 character
                            uint address = GPR[4];       //address of the string is in $a0
                            if (address == 0) {
                                Console.Write("\\<NULL>");
                            }
                            else {

                                while (BUS.LoadByte(address) != 0) {
                                    character = (char)BUS.LoadByte(address);
                                    Console.Write(character);
                                    address++;
                                }

                            }
                            break;

                        case 0xB:
                            /*if (BUS.debug) {
                                Console.WriteLine("TestEvent");
                                Console.WriteLine("$a0: " + GPR[4].ToString("X"));
                                Console.WriteLine("$a1: " + GPR[5].ToString("X"));
                                Console.WriteLine("$a2: " + GPR[6].ToString("X"));
                                Console.WriteLine("$a3: " + GPR[7].ToString("X"));                              
                            }*/
                            break;

                     
                        case 0x08:
                         
                            /*Console.WriteLine("OpenEvent");
                            Console.WriteLine("$a0: " + GPR[4].ToString("X"));
                            Console.WriteLine("$a1: " + GPR[5].ToString("X"));
                            Console.WriteLine("$a2: " + GPR[6].ToString("X"));
                            Console.WriteLine("$a3: " + GPR[7].ToString("X"));*/
                            
                            break;

                        default:
                            if (BUS.debug) {
                                Console.WriteLine("Function B: " + GPR[9].ToString("x"));
                            }
                            break;
                    }

                    break;

                case 0xC0:
                    if (BUS.debug) {
                        Console.WriteLine("Function C: " + GPR[9].ToString("x"));
                    }
                    break;
            }
        }

        private void loadTestRom(string? path) {
            EXE = File.ReadAllBytes(path);

            //Copy the EXE data to memory
            uint addressInRAM = (uint)(EXE[0x018] | EXE[0x018 + 1] << 8 |  EXE[0x018 + 2] << 16 | EXE[0x018 + 3] << 24);

            for (int i = 0x800; i < EXE.Length; i++) {
                BUS.StoreByte(addressInRAM, EXE[i]);
                addressInRAM++;
            }
         
            //Set up SP, FP, and GP
            uint baseStackAndFrameAddress = (uint)(EXE[0x30] | EXE[0x30 + 1] << 8 | EXE[0x30 + 2] << 16 | EXE[0x30 + 3] << 24);

            if (baseStackAndFrameAddress != 0) {              
                uint stackAndFrameOffset = (uint)(EXE[0x34] | EXE[0x34 + 1] << 8 | EXE[0x34 + 2] << 16 | EXE[0x34 + 3] << 24);
                GPR[(int)CPU.Register.sp] = GPR[(int)CPU.Register.fp] = baseStackAndFrameAddress + stackAndFrameOffset;
            }

            GPR[(int)CPU.Register.gp] = (uint)(EXE[0x14] | EXE[0x14 + 1] << 8 | EXE[0x14 + 2] << 16 | EXE[0x14 + 3] << 24);

            //Jump to the address specified by the EXE
            Current_PC = PC = (uint)(EXE[0x10] | EXE[0x10 + 1] << 8 | EXE[0x10 + 2] << 16 | EXE[0x10 + 3] << 24);
            Next_PC = PC + 4;
        }

        private static void cop0(CPU_Interpreter cpu, Instruction instruction) {
            switch (instruction.Get_rs()) {
                case 0b00100:
                    mtc0(cpu, instruction);
                    break;

                case 0b00000:
                    mfc0(cpu, instruction);
                    break;

                case 0b10000:
                    rfe(cpu, instruction);
                    break;

                default: throw new Exception("Unhandled cop0 instruction: " + instruction.FullValue.ToString("X"));
            }
        }

        private static void illegal(CPU_Interpreter cpu, Instruction instruction) {
            Console.ForegroundColor = ConsoleColor.Red; 
            Console.WriteLine("[CPU] Illegal instruction: " + instruction.FullValue.ToString("X").PadLeft(8,'0') + " - Opcode(" + instruction.GetOpcode().ToString("X") + ") - " + " at PC: " + cpu.Current_PC.ToString("x"));
            Console.ForegroundColor = ConsoleColor.Green;
            Exception(cpu, (uint)CPU.Exceptions.IllegalInstruction);
            cpu.IsStopped = true;
        }

        private static void swc3(CPU_Interpreter cpu, Instruction instruction) {
            Exception(cpu, (uint)CPU.Exceptions.CoprocessorError); //StoreWord is not supported in this cop
        }

        private static void swc2(CPU_Interpreter cpu, Instruction instruction) {
            uint address = cpu.GPR[instruction.Get_rs()] + instruction.GetSignedImmediate();

            if ((address & 0x3) != 0) {
                Exception(cpu, (uint)CPU.Exceptions.LoadAddressError);
                return;
            }

            uint rt = instruction.Get_rt();
            uint word = cpu.GTE.read(rt);
            cpu.BUS.StoreWord(address, word);
        }

        private static void swc1(CPU_Interpreter cpu, Instruction instruction) {
            Exception(cpu, (uint)CPU.Exceptions.CoprocessorError); //StoreWord is not supported in this cop
        }

        private static void swc0(CPU_Interpreter cpu, Instruction instruction) {
            Exception(cpu, (uint)CPU.Exceptions.CoprocessorError); //StoreWord is not supported in this cop
        }

        private static void lwc3(CPU_Interpreter cpu, Instruction instruction) {
            Exception(cpu, (uint)CPU.Exceptions.CoprocessorError); //LoadWord is not supported in this cop
        }

        private static void lwc2(CPU_Interpreter cpu, Instruction instruction) {
            //TODO add 2 instructions delay
            uint address = cpu.GPR[instruction.Get_rs()] + instruction.GetSignedImmediate();

            if ((address & 0x3) != 0) {
                Exception(cpu, (uint)CPU.Exceptions.LoadAddressError);
                return;
            }

            uint word = cpu.BUS.LoadWord(address);
            uint rt = instruction.Get_rt();
            cpu.GTE.write(rt, word);

        }

        private static void lwc1(CPU_Interpreter cpu, Instruction instruction) {
            Exception(cpu, (uint)CPU.Exceptions.CoprocessorError); //LoadWord is not supported in this cop
        }

        private static void lwc0(CPU_Interpreter cpu, Instruction instruction) {
            Exception(cpu, (uint)CPU.Exceptions.CoprocessorError); //LoadWord is not supported in this cop
        }

        private static void swr(CPU_Interpreter cpu, Instruction instruction) {
            if (cpu.IscIsolateCache) { return; }

            //TODO add 2 instructions delay

            uint addressRegPos = instruction.GetSignedImmediate();
            uint base_ = instruction.Get_rs();
            uint final_address = cpu.GPR[base_] + addressRegPos;

            uint value =  cpu.GPR[instruction.Get_rt()];               
            uint current_value = cpu.BUS.LoadWord((uint)(final_address & ~3));     //Last 2 bits are for alignment position only 

            uint finalValue;
            uint pos = final_address & 3;

            switch (pos) {
                case 0: finalValue = current_value & 0x00000000 | value << 0; break;
                case 1: finalValue = current_value & 0x000000ff | value << 8; break;
                case 2: finalValue = current_value & 0x0000ffff | value << 16; break;
                case 3: finalValue = current_value & 0x00ffffff | value << 24; break;
                default: throw new Exception("swl instruction error, pos:" + pos);
            }

            cpu.BUS.StoreWord((uint)(final_address & ~3), finalValue);
        }

        private static void swl(CPU_Interpreter cpu, Instruction instruction) {
            if (cpu.IscIsolateCache) { return; }

            uint addressRegPos = instruction.GetSignedImmediate();
            uint base_ = instruction.Get_rs();
            uint final_address = cpu.GPR[base_] + addressRegPos;

            uint value = cpu.GPR[instruction.Get_rt()];           
            uint current_value = cpu.BUS.LoadWord((uint)(final_address&~3));     //Last 2 bits are for alignment position only 

            uint finalValue;
            uint pos = final_address & 3;

            switch (pos) {
                case 0: finalValue = current_value & 0xffffff00 | value >> 24; break;
                case 1: finalValue = current_value & 0xffff0000 | value >> 16; break;
                case 2: finalValue = current_value & 0xff000000 | value >> 8; break;
                case 3: finalValue = current_value & 0x00000000 | value >> 0; break;
                default: throw new Exception("swl instruction error, pos:" + pos);
            }

            cpu.BUS.StoreWord((uint)(final_address & ~3), finalValue);
        }

        private static void lwr(CPU_Interpreter cpu, Instruction instruction) {
            if (cpu.IscIsolateCache) { return; }

            uint imm = instruction.GetSignedImmediate();
            uint rs = instruction.Get_rs();
            uint rt = instruction.Get_rt();

            uint final_address = cpu.GPR[rs] + imm;

            uint current_value = cpu.GPR[rt];

            if (rt == cpu.ReadyRegisterLoad.RegisterNumber) {
                current_value = cpu.ReadyRegisterLoad.Value;                         //Bypass load delay
            }

            uint word = cpu.BUS.LoadWord((uint)(final_address & ~3));     //Last 2 bits are for alignment position only 
            uint finalValue;
            uint pos = final_address & 3;

            switch (pos) {
                case 0: finalValue = current_value & 0x00000000 | word >> 0; break;
                case 1: finalValue = current_value & 0xff000000 | word >> 8; break;
                case 2: finalValue = current_value & 0xffff0000 | word >> 16; break;
                case 3: finalValue = current_value & 0xffffff00 | word >> 24; break;
                default: throw new Exception("lwr instruction error, pos:" + pos);
            }

            cpu.DelayedRegisterLoad.RegisterNumber = rt;                     //Position
            cpu.DelayedRegisterLoad.Value = finalValue;                      //Value
        }

        private static void lwl(CPU_Interpreter cpu, Instruction instruction) {
            if (cpu.IscIsolateCache) { return; }
            uint imm = instruction.GetSignedImmediate();
            uint rs = instruction.Get_rs();
            uint rt = instruction.Get_rt();
            uint final_address = cpu.GPR[rs] + imm;

            uint current_value =  cpu.GPR[rt];

            if (rt == cpu.ReadyRegisterLoad.RegisterNumber) {
                current_value = cpu.ReadyRegisterLoad.Value;             //Bypass load delay
            }

            uint word = cpu.BUS.LoadWord((uint)(final_address&~3));     //Last 2 bits are for alignment position only 
            uint finalValue;
            uint pos = final_address & 3;

            switch (pos) {
                case 0: finalValue = current_value & 0x00ffffff | word << 24; break;
                case 1: finalValue = current_value & 0x0000ffff | word << 16; break;
                case 2: finalValue = current_value & 0x000000ff | word << 8; break;
                case 3: finalValue = current_value & 0x00000000 | word << 0; break;
                default: throw new Exception("lwl instruction error, pos:" + pos);
            }

            cpu.DelayedRegisterLoad.RegisterNumber = rt;                    //Position
            cpu.DelayedRegisterLoad.Value = finalValue;                      //Value           
        }

        private static void cop2(CPU_Interpreter cpu, Instruction instruction) {

            /*if (((instruction.Get_rs() >> 4) & 1) == 1) {    //COP2 imm25 command
                cpu.GTE.execute(instruction);
                return;
            }*/

            if (instruction.FullValue >> 25 == 0b0100101) {    //COP2 imm25 command
                cpu.GTE.execute(instruction.FullValue);
                return;
            }

            //GTE registers reads/writes have delay of 1 (?) instruction

            switch (instruction.Get_rs()) {
                
                case 0b00000:   //MFC                
                    cpu.DelayedRegisterLoad.RegisterNumber = instruction.Get_rt();
                    cpu.DelayedRegisterLoad.Value = cpu.GTE.read(instruction.Get_rd());
                    break;

                case 0b00010:   //CFC
                    cpu.DelayedRegisterLoad.RegisterNumber = instruction.Get_rt();
                    cpu.DelayedRegisterLoad.Value = cpu.GTE.read(instruction.Get_rd() + 32);
                    break;

                case 0b00110:  //CTC 
                    uint rd = instruction.Get_rd();
                    uint value = cpu.GPR[instruction.Get_rt()];
                    cpu.GTE.write(rd + 32, value);
                    break;

                case 0b00100:  //MTC 
                    rd = instruction.Get_rd();
                    value = cpu.GPR[instruction.Get_rt()];
                    cpu.GTE.write(rd, value);   //Same as CTC but without adding 32 to the position
                    break;

                default:  throw new Exception("Unhandled GTE opcode: " + instruction.Get_rs().ToString("X"));
            }
        }

        private static void cop3(CPU_Interpreter cpu, Instruction instruction) {
            Exception(cpu, (uint)CPU.Exceptions.CoprocessorError);
        }

        private static void cop1(CPU_Interpreter cpu, Instruction instruction) {
            Exception(cpu, (uint)CPU.Exceptions.CoprocessorError);
        }

        private static void xori(CPU_Interpreter cpu, Instruction instruction) {
            uint imm = instruction.GetImmediate();
            cpu.DirectWrite.RegisterNumber = instruction.Get_rt();         //Position
            cpu.DirectWrite.Value = cpu.GPR[instruction.Get_rs()] ^ imm;  //Value
        }

        private static void lh(CPU_Interpreter cpu, Instruction instruction) {
            if (cpu.IscIsolateCache) { return; }
            uint addressRegPos = instruction.GetSignedImmediate();
            uint base_ = instruction.Get_rs();
            uint final_address = cpu.GPR[base_] + addressRegPos;
               
            //aligned?
            short halfWord = (short)cpu.BUS.LoadHalf(final_address);
            if ((final_address & 0x1) == 0) {
                cpu.DelayedRegisterLoad.RegisterNumber = instruction.Get_rt();         //Position
                cpu.DelayedRegisterLoad.Value = (uint)halfWord;                     //Value
            } else {
                Exception(cpu, (uint)CPU.Exceptions.LoadAddressError);
            }
        }

        private static void lhu(CPU_Interpreter cpu, Instruction instruction) {
            if (cpu.IscIsolateCache) { return; }

            uint addressRegPos = instruction.GetSignedImmediate();
            uint base_ = instruction.Get_rs();
            uint final_address = cpu.GPR[base_] + addressRegPos;

            if ((final_address & 0x1) == 0) {
                uint halfWord = cpu.BUS.LoadHalf(final_address);
                cpu.DelayedRegisterLoad.RegisterNumber = instruction.Get_rt();  //Position
                cpu.DelayedRegisterLoad.Value = halfWord;                       //Value
               
            }
            else {
                Exception(cpu, (uint)CPU.Exceptions.LoadAddressError);
            }
        }

        private static void sltiu(CPU_Interpreter cpu, Instruction instruction) {
            cpu.DirectWrite.RegisterNumber = instruction.Get_rt();

            if (cpu.GPR[instruction.Get_rs()] < instruction.GetSignedImmediate()) {
                cpu.DirectWrite.Value = 1;
            } else {
                cpu.DirectWrite.Value = 0;
            }
        }

        private static void sub(CPU_Interpreter cpu, Instruction instruction) {
            int reg1 = (int)cpu.GPR[instruction.Get_rs()];
            int reg2 = (int)cpu.GPR[instruction.Get_rt()];

            try {
                int value = checked(reg1 - reg2);        //Check for signed integer overflow 
                cpu.DirectWrite.RegisterNumber = instruction.Get_rd();
                cpu.DirectWrite.Value = (uint)value;
            }
            catch (OverflowException) {
                Exception(cpu, (uint)CPU.Exceptions.Overflow);
            }        
        }

        private static void mult(CPU_Interpreter cpu, Instruction instruction) {
            uint rs = instruction.Get_rs();
            uint rt = instruction.Get_rt();

            //Sign extend
            long a =  (int)cpu.GPR[rs];
            long b =  (int)cpu.GPR[rt];

            ulong v = (ulong)(a * b);

            cpu.HI = (uint)(v >> 32);
            cpu.LO = (uint)v;
        }

        private static void break_(CPU_Interpreter cpu, Instruction instruction) {
            Exception(cpu, (uint)CPU.Exceptions.Break);
        }

        private static void xor(CPU_Interpreter cpu, Instruction instruction) {
            cpu.DirectWrite.RegisterNumber = instruction.Get_rd();
            cpu.DirectWrite.Value = cpu.GPR[instruction.Get_rs()] ^ cpu.GPR[instruction.Get_rt()];
        }

        private static void multu(CPU_Interpreter cpu, Instruction instruction) {
            uint rs = instruction.Get_rs();
            uint rt = instruction.Get_rt();

            ulong a = cpu.GPR[rs];
            ulong b = cpu.GPR[rt];

            ulong v = a * b;

            cpu.HI = (uint)(v >> 32);
            cpu.LO = (uint)v;
        }

        private static void srlv(CPU_Interpreter cpu, Instruction instruction) {
            uint rs = instruction.Get_rs();
            uint rt = instruction.Get_rt();
            uint rd = instruction.Get_rd();

            cpu.DirectWrite.RegisterNumber = rd;
            cpu.DirectWrite.Value = cpu.GPR[rt] >> (int)(cpu.GPR[rs] & 0x1f);
        }

        private static void srav(CPU_Interpreter cpu, Instruction instruction) {
            uint rs = instruction.Get_rs();
            uint rt = instruction.Get_rt();
            uint rd = instruction.Get_rd();

            int value = (int)cpu.GPR[rt] >> (int)(cpu.GPR[rs] & 0x1f);
            cpu.DirectWrite.RegisterNumber = rd;
            cpu.DirectWrite.Value = (uint)value;
        }

        private static void nor(CPU_Interpreter cpu, Instruction instruction) {
            cpu.DirectWrite.RegisterNumber = instruction.Get_rd();
            cpu.DirectWrite.Value = ~(cpu.GPR[instruction.Get_rs()] | cpu.GPR[instruction.Get_rt()]);
        }

        private static void sllv(CPU_Interpreter cpu, Instruction instruction) {
            uint rs = instruction.Get_rs();
            uint rt = instruction.Get_rt();
            uint rd = instruction.Get_rd();

            cpu.DirectWrite.RegisterNumber = rd;             //Take 5 bits from register rs
            cpu.DirectWrite.Value = cpu.GPR[rt] << (int)(cpu.GPR[rs] & 0x1f);
        }

        private static void mthi(CPU_Interpreter cpu, Instruction instruction) {
            uint rs = instruction.Get_rs();
            cpu.HI = cpu.GPR[rs];
        }

        private static void mtlo(CPU_Interpreter cpu, Instruction instruction) {
            uint rs = instruction.Get_rs();
            cpu.LO = cpu.GPR[rs];
        }

        private static void syscall(CPU_Interpreter cpu,Instruction instruction) {
            Exception(cpu, (uint)CPU.Exceptions.SysCall);
        }

        public static void Exception(CPU_Interpreter cpu, uint exceptionCause){
            //If the next instruction is a GTE instruction skip the exception
            //Otherwise the BIOS will try to handle the GTE bug by skipping the instruction  
           
            uint handler;                                         //Get the handler

            if ((cpu.Cop0.SR & 1 << 22) != 0) {
                handler = 0xbfc00180;
            } else {
                handler = 0x80000080;
            }
  
            uint mode = cpu.Cop0.SR & 0x3f;                          //Disable interrupts 

            cpu.Cop0.SR = (uint)(cpu.Cop0.SR & ~0x3f);

            cpu.Cop0.SR = cpu.Cop0.SR | mode << 2 & 0x3f;


            cpu.Cop0.Cause = exceptionCause << 2;                    //Update cause register

            cpu.Cop0.EPC = cpu.Current_PC;                 //Save the current PC in register EPC

            //Hack
            if (exceptionCause == (int)CPU.Exceptions.IRQ) {
                cpu.Cop0.EPC = cpu.PC;           //Save the PC in register EPC
                cpu.DelaySlot = cpu.Branch;
            } else {
                cpu.Cop0.EPC = cpu.Current_PC;    //Save the current PC in register EPC                                                  
            }

            if (cpu.DelaySlot) {                   //in case an exception occurs in a delay slot
                cpu.Cop0.EPC -= 4;
                cpu.Cop0.Cause = (uint)(cpu.Cop0.Cause | 1 << 31);
            }

            cpu.PC = handler;                          //Jump to the handler address (no delay)
            cpu.Next_PC = cpu.PC + 4;
        }

        private static void slt(CPU_Interpreter cpu, Instruction instruction) {
            cpu.DirectWrite.RegisterNumber = instruction.Get_rd();
            if ((int)cpu.GPR[instruction.Get_rs()] < (int)cpu.GPR[instruction.Get_rt()]) {
                cpu.DirectWrite.Value = 1;
            }
            else {
                cpu.DirectWrite.Value = 0;
            }
        }

        private static void divu(CPU_Interpreter cpu, Instruction instruction) {
            uint rs = instruction.Get_rs();
            uint rt = instruction.Get_rt();

            uint numerator = cpu.GPR[rs];
            uint denominator = cpu.GPR[rt];

            if (denominator == 0) {
                cpu.LO = 0xffffffff;
                cpu.HI = numerator;
                return;
            }

            cpu.LO = numerator / denominator;
            cpu.HI = numerator % denominator;

        }

        private static void srl(CPU_Interpreter cpu, Instruction instruction) {
            //Right Shift (Logical)
            uint rt = instruction.Get_rt();
            uint rd = instruction.Get_rd();
            uint sa = instruction.Get_sa();

            uint val = cpu.GPR[rt];
            cpu.DirectWrite.RegisterNumber = rd;
            cpu.DirectWrite.Value = val >> (int)sa;
        }

        private static void mflo(CPU_Interpreter cpu, Instruction instruction) { //LO -> GPR[rd]
            uint rd = instruction.Get_rd();
            cpu.DirectWrite.RegisterNumber = rd;
            cpu.DirectWrite.Value = cpu.LO;
        }

        private static void mfhi(CPU_Interpreter cpu, Instruction instruction) {        //HI -> GPR[rd]
            uint rd = instruction.Get_rd();
            cpu.DirectWrite.RegisterNumber = rd;
            cpu.DirectWrite.Value = cpu.HI;
        }

        private static void div(CPU_Interpreter cpu, Instruction instruction) { // GPR[rs] / GPR[rt] -> (HI, LO) 
            uint rs = instruction.Get_rs();
            uint rt = instruction.Get_rt();

            int numerator = (int)cpu.GPR[rs];
            int denominator = (int)cpu.GPR[rt];

            if (numerator >= 0 && denominator == 0) {
                cpu.LO = 0xffffffff;
                cpu.HI = (uint)numerator;
                return;
            }
            else if (numerator < 0 && denominator == 0) {
                cpu.LO = 1;
                cpu.HI = (uint)numerator;
                return;
            }
            else if ((uint)numerator == 0x80000000 && (uint)denominator == 0xffffffff) {
                cpu.LO = 0x80000000;
                cpu.HI = 0;
                return;
            }

            cpu.LO = (uint)unchecked(numerator / denominator);
            cpu.HI = (uint)unchecked(numerator % denominator);
        }

        private static void sra(CPU_Interpreter cpu, Instruction instruction) {
            //Right Shift (Arithmetic)
            uint rt = instruction.Get_rt();
            uint rd = instruction.Get_rd();
            uint sa = instruction.Get_sa();

            int val = (int)cpu.GPR[rt];
            cpu.DirectWrite.RegisterNumber = rd;
            cpu.DirectWrite.Value = (uint)(val >> (int)sa);
        }

        private static void slti(CPU_Interpreter cpu, Instruction instruction) {
            int si = (int)instruction.GetSignedImmediate();
            int rg = (int)cpu.GPR[instruction.Get_rs()];
            cpu.DirectWrite.RegisterNumber = instruction.Get_rt();

            if (rg<si) {
                cpu.DirectWrite.Value = 1;
            } else {
                cpu.DirectWrite.Value = 0;
            }
        }

        private static void bxx(CPU_Interpreter cpu,Instruction instruction) {      
            bool bgez = (instruction.FullValue >> 16 & 1) == 1;
            bool link = (instruction.FullValue >> 17 & 0xF) == 0x8;
            uint linkAddress = cpu.Next_PC;             //Save Next_PC before a branch overwrites it

            //if rs is $ra, then the value used for the comparison is $ra's value before linking.
            if (bgez) {
                //BGEZ
                if ((int)cpu.GPR[instruction.Get_rs()] >= 0) {
                    branch(cpu,instruction.GetSignedImmediate());
                }
            } else {
                //BLTZ
                if ((int)cpu.GPR[instruction.Get_rs()] < 0) {
                    branch(cpu,instruction.GetSignedImmediate());
                }
            }

            if (link) {
               //Store return address in $31 if the value of bits [20:17] == 0x8
                cpu.DirectWrite.RegisterNumber = (uint)CPU.Register.ra;
                cpu.DirectWrite.Value = linkAddress;
            }
        }

        private static void lbu(CPU_Interpreter cpu, Instruction instruction) {
            if (cpu.IscIsolateCache) { return; }
            uint addressRegPos = instruction.GetSignedImmediate();
            uint base_ = instruction.Get_rs();

            byte byte_ = cpu.BUS.LoadByte(cpu.GPR[base_] + addressRegPos);
            cpu.DelayedRegisterLoad.RegisterNumber = instruction.Get_rt();  //Position
            cpu.DelayedRegisterLoad.Value = byte_;                     //Value        
        }

        private static void blez(CPU_Interpreter cpu, Instruction instruction) {
            int signedValue = (int)cpu.GPR[instruction.Get_rs()];
            if (signedValue <= 0) {
                branch(cpu,instruction.GetSignedImmediate());
            }
        }

        private static void bgtz(CPU_Interpreter cpu, Instruction instruction) {     //Branch if > 0
            int signedValue = (int)cpu.GPR[instruction.Get_rs()];      
            if (signedValue > 0) {
                branch(cpu,instruction.GetSignedImmediate());
            }
        }

        private static void subu(CPU_Interpreter cpu, Instruction instruction) {
            cpu.DirectWrite.RegisterNumber = instruction.Get_rd();
            cpu.DirectWrite.Value = cpu.GPR[instruction.Get_rs()] - cpu.GPR[instruction.Get_rt()];
        }

        private static void jalr(CPU_Interpreter cpu, Instruction instruction) {
            uint rs = instruction.Get_rs();
            uint rd = instruction.Get_rd();

            // Store return address in $rd
            cpu.DirectWrite.RegisterNumber = rd;
            cpu.DirectWrite.Value = cpu.Next_PC;

            if ((cpu.GPR[instruction.Get_rs()] & 0x3) != 0) {
                Exception(cpu, (uint)CPU.Exceptions.LoadAddressError);
                return;
            }
            // Jump to address in $rs
            cpu.Next_PC = cpu.GPR[rs];
            cpu.Branch = true;
        }

        private static void beq(CPU_Interpreter cpu, Instruction instruction) {
            if (cpu.GPR[instruction.Get_rs()].Equals(cpu.GPR[instruction.Get_rt()])) {
                branch(cpu,instruction.GetSignedImmediate());
            }
        }

        private static void lb(CPU_Interpreter cpu, Instruction instruction) {
            if (cpu.IscIsolateCache) { return; }
            uint addressRegPos = instruction.GetSignedImmediate();
            uint base_ = instruction.Get_rs();
            sbyte sb = (sbyte)cpu.BUS.LoadByte(cpu.GPR[base_] + addressRegPos);
            cpu.DelayedRegisterLoad.RegisterNumber = instruction.Get_rt();  //Position
            cpu.DelayedRegisterLoad.Value = (uint)sb;                     //Value
        }

        private static void sb(CPU_Interpreter cpu, Instruction instruction) {
            if (cpu.IscIsolateCache) { return; }

            uint targetReg = instruction.Get_rt();
            uint addressRegPos = instruction.GetSignedImmediate();
            uint base_ = instruction.Get_rs();
            cpu.BUS.StoreByte(cpu.GPR[base_] + addressRegPos, (byte)cpu.GPR[targetReg]);
        }

        private static void andi(CPU_Interpreter cpu,Instruction instruction) {
            uint targetReg = instruction.Get_rt();
            uint imm = instruction.GetImmediate();
            uint rs = instruction.Get_rs();
            cpu.DirectWrite.RegisterNumber = targetReg;
            cpu.DirectWrite.Value = cpu.GPR[rs] & imm;
        }

        private static void jal(CPU_Interpreter cpu, Instruction instruction) {
            cpu.DirectWrite.RegisterNumber = (uint)CPU.Register.ra;
            cpu.DirectWrite.Value = cpu.Next_PC;             //Jump and link, store the PC to return to it later
            cpu.Next_PC = cpu.Next_PC & 0xf0000000 | instruction.GetImmediateJumpAddress() << 2;
            cpu.Branch = true;
        }

        private static void sh(CPU_Interpreter cpu, Instruction instruction) {
            if (cpu.IscIsolateCache) { return; }

            uint targetReg = instruction.Get_rt();

            uint addressRegPos = instruction.GetSignedImmediate();
            uint base_ = instruction.Get_rs();
            uint final_address = cpu.GPR[base_] + addressRegPos;

            //Address must be 16 bit aligned
            if ((final_address & 1) == 0) {
                cpu.BUS.StoreHalf(final_address, (ushort)cpu.GPR[targetReg]);
            }
            else {
                Exception(cpu, (uint)CPU.Exceptions.StoreAddressError);
            }
        }

        private static void addi(CPU_Interpreter cpu, Instruction instruction) {
            int imm = (int)instruction.GetSignedImmediate();
            int s = (int)cpu.GPR[instruction.Get_rs()];
             try {
                 int value = checked(imm + s);        //Check for signed integer overflow 
                 cpu.DirectWrite.RegisterNumber = instruction.Get_rt();
                 cpu.DirectWrite.Value = (uint)value;
             }
             catch (OverflowException) {
                 Exception(cpu, (uint)CPU.Exceptions.Overflow);
             }          
        }

        public static void lui(CPU_Interpreter cpu, Instruction instruction) {            
            uint value = instruction.GetImmediate();
            cpu.DirectWrite.RegisterNumber = instruction.Get_rt();
            cpu.DirectWrite.Value = value << 16;
        }

        public static void ori(CPU_Interpreter cpu, Instruction instruction) {
            uint value = instruction.GetImmediate();
            uint rs = instruction.Get_rs();
            cpu.DirectWrite.RegisterNumber = instruction.Get_rt();
            cpu.DirectWrite.Value = cpu.GPR[rs] | value;
        }

        public static void or(CPU_Interpreter cpu, Instruction instruction) {
            cpu.DirectWrite.RegisterNumber = instruction.Get_rd();
            cpu.DirectWrite.Value = cpu.GPR[instruction.Get_rs()] | cpu.GPR[instruction.Get_rt()];
        }

        private static void and(CPU_Interpreter cpu, Instruction instruction) {
            cpu.DirectWrite.RegisterNumber = instruction.Get_rd();
            cpu.DirectWrite.Value = cpu.GPR[instruction.Get_rs()] & cpu.GPR[instruction.Get_rt()];
        }

        public static void sw(CPU_Interpreter cpu, Instruction instruction) {
            if (cpu.IscIsolateCache) { return; }

            uint targetReg = instruction.Get_rt();

            uint addressRegPos = instruction.GetSignedImmediate();
            uint base_ = instruction.Get_rs();
            uint final_address = cpu.GPR[base_] + addressRegPos;

            //Address must be 32 bit aligned
            if ((final_address & 0x3) == 0) {
                cpu.BUS.StoreWord(final_address, cpu.GPR[targetReg]);
            }
            else {
                Exception(cpu, (uint)CPU.Exceptions.StoreAddressError);
            }
        }

        public static void lw(CPU_Interpreter cpu, Instruction instruction) {
            if (cpu.IscIsolateCache) { return; }

            uint addressRegPos = instruction.GetSignedImmediate();
            uint base_ = instruction.Get_rs();
            uint final_address = cpu.GPR[base_] + addressRegPos;
       
            //Address must be 32 bit aligned
            if ((final_address & 0x3) == 0) {
                 cpu.DelayedRegisterLoad.RegisterNumber = instruction.Get_rt();              //Position
                 cpu.DelayedRegisterLoad.Value = cpu.BUS.LoadWord(final_address);           //Value
            }
            else {
                Exception(cpu, (uint)CPU.Exceptions.LoadAddressError);
            }
        }
        
        private static void add(CPU_Interpreter cpu, Instruction instruction) {
            int reg1 = (int)cpu.GPR[instruction.Get_rs()];       
            int reg2 = (int)cpu.GPR[instruction.Get_rt()];
            try {
                int value = checked(reg1 + reg2);        //Check for signed integer overflow, can be ignored as no games rely on this 
                cpu.DirectWrite.RegisterNumber = instruction.Get_rd();
                cpu.DirectWrite.Value = (uint)value;
            }
            catch (OverflowException) {
                Exception(cpu, (uint)CPU.Exceptions.Overflow);    
            }
        }

        private static void jr(CPU_Interpreter cpu, Instruction instruction) {
            uint rs = instruction.Get_rs();

            cpu.Next_PC = cpu.GPR[rs];      //Return or Jump to address in register 
            if ((cpu.Next_PC & 0x3) != 0) {
                Exception(cpu, (uint)CPU.Exceptions.LoadAddressError);
            }
            cpu.Branch = true;
        }

        private static void addu(CPU_Interpreter cpu, Instruction instruction) {
            cpu.DirectWrite.RegisterNumber = instruction.Get_rd();
            cpu.DirectWrite.Value = cpu.GPR[instruction.Get_rs()] + cpu.GPR[instruction.Get_rt()];
        }

        private static void sltu(CPU_Interpreter cpu, Instruction instruction) {
            cpu.DirectWrite.RegisterNumber = instruction.Get_rd();
            if (cpu.GPR[instruction.Get_rs()] < cpu.GPR[instruction.Get_rt()]) {
                cpu.DirectWrite.Value = 1;
            }
            else {
                cpu.DirectWrite.Value = 0;
            }          
        }

        public static void sll(CPU_Interpreter cpu,Instruction instruction) {
            uint rt = instruction.Get_rt();
            uint rd = instruction.Get_rd();
            uint sa = instruction.Get_sa();

            cpu.DirectWrite.RegisterNumber = rd;
            cpu.DirectWrite.Value = cpu.GPR[rt] << (int)sa;
        }

        private static void addiu(CPU_Interpreter cpu, Instruction instruction) {
            cpu.DirectWrite.RegisterNumber = instruction.Get_rt();
            cpu.DirectWrite.Value = cpu.GPR[instruction.Get_rs()] + instruction.GetSignedImmediate();
        }

        private static void jump(CPU_Interpreter cpu, Instruction instruction) {
            cpu.Next_PC = cpu.Next_PC & 0xf0000000 | instruction.GetImmediateJumpAddress() << 2;
            cpu.Branch = true;
        }

        private static void rfe(CPU_Interpreter cpu, Instruction instruction) {
            if (instruction.Get_Subfunction() != 0b010000) {    //Check bits [5:0]
                throw new Exception("Invalid cop0 instruction: " + instruction.FullValue.ToString("X"));
            }
                         /*
                        uint mode = cpu.SR & 0x3f;                   
                        cpu.SR = (uint)(cpu.SR & ~0x3f);
                        cpu.SR = cpu.SR | (mode >> 2);*/

            uint temp = cpu.Cop0.SR;
            cpu.Cop0.SR = (uint)(cpu.Cop0.SR & ~0xF);
            cpu.Cop0.SR |= temp >> 2 & 0xF;
        }

        private static void mfc0(CPU_Interpreter cpu, Instruction instruction) {
            //MFC has load delay
            cpu.DelayedRegisterLoad.RegisterNumber = instruction.Get_rt();

            switch (instruction.Get_rd()) {
                case 12: cpu.DelayedRegisterLoad.Value = cpu.Cop0.SR; break;
                case 13: cpu.DelayedRegisterLoad.Value = cpu.Cop0.Cause; break;
                case 14: cpu.DelayedRegisterLoad.Value = cpu.Cop0.EPC; break;
                case 15: cpu.DelayedRegisterLoad.Value = 0x00000002; break;     //COP0 R15 (PRID)
                default: cpu.DelayedRegisterLoad.RegisterNumber = 0; Console.WriteLine("Unhandled cop0 Register Read: " + instruction.Get_rd()); break;
            }
        }

        private static void mtc0(CPU_Interpreter cpu, Instruction instruction) {

            switch (instruction.Get_rd()) {
                case 3:
                case 5:                          //Breakpoints registers
                case 6:
                case 7:
                case 9:
                case 11:
                    if (cpu.GPR[instruction.Get_rt()] != 0) {
                        //throw new Exception("Unhandled write to cop0 register: " + instruction.Get_rd());
                    }
                    break;

                case 12: cpu.Cop0.SR = cpu.GPR[instruction.Get_rt()]; break;         //Setting the status register's 16th bit

                case 13:
                    //cause register, mostly read-only data describing the
                    //cause of an exception. Apparently only bits[9:8] are writable
                    if (cpu.GPR[instruction.Get_rt()] != 0) { 
                        //throw new Exception("Unhandled write to CAUSE register: " + instruction.get_rd());
                    }
                    break;

                default: throw new Exception("Unhandled cop0 register: " + instruction.Get_rd());
            }
        }
        private static void bne(CPU_Interpreter cpu, Instruction instruction) {
            if (!cpu.GPR[instruction.Get_rs()].Equals(cpu.GPR[instruction.Get_rt()])) {
                branch(cpu,instruction.GetSignedImmediate());
            }
        }

        public static void branch(CPU_Interpreter cpu, uint offset) {
            offset = offset << 2;
            cpu.Next_PC = cpu.Next_PC + offset;
            cpu.Next_PC = cpu.Next_PC - 4;        //Cancel the +4 from the emu cycle 
            cpu.Branch = true;    
        }

        public void TickFrame() {
            ulong currentTime = CurrentCycle;
            ulong endFrameTime = currentTime + CYCLES_PER_FRAME;

            while (currentTime < endFrameTime) {
                //Get the next event
                ScheduledEvent nextEvent = Scheduler.DequeueNearestEvent();

                //Run the CPU until the event
                while (CurrentCycle < nextEvent.EndTime) {
                    emu_cycle();
                }

                //Handle the ready event and check for interrupts
                nextEvent.Callback();
                IRQCheck();

                //Update current time
                currentTime = CurrentCycle;
            }

            CyclesDone += CYCLES_PER_FRAME;
        }

        public void Reset() {
            Current_PC = PC = 0xbfc00000;
            Next_PC = PC + 4;
        }

        public ref BUS GetBUS() {
            return ref BUS;
        }

        public double GetSpeed() {
            double returnValue = (CyclesDone / CYCLES_PER_SECOND) * 100;
            CyclesDone = 0;
            return returnValue;
        }

        public void SetInvalidAllRAMBlocks() {
            //Unsupported
            return;
        }

        public void SetInvalidRAMBlock(uint block) {
            //Unsupported
            return;
        }

        public void SetInvalidAllBIOSBlocks() {
            //Unsupported
            return;
        }

        public ulong GetCurrentCycle() {
            return CurrentCycle;
        }
    }
}
