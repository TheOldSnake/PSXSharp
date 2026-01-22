using Iced.Intel;
using System;
using System.IO;
using System.Runtime.InteropServices;
using Instruction = PSXSharp.Core.Common.Instruction;
using Label = Iced.Intel.Label;

namespace PSXSharp.Core.x64_Recompiler {
    public unsafe partial class CPU_x64_Recompiler : CPU, IDisposable {
        private bool Disposed = false;
        public const uint RESET_VECTOR = 0xBFC00000;
        public const uint BIOS_START = 0x1FC00000;          //Reset vector but masked
        public const uint SHELL_START = 0x80030000;         //Shell start address - we can load EXE if we reach it
        public const uint A_FunctionsTableAddress = 0xA0;   //A-Functions Table
        public const uint B_FunctionsTableAddress = 0xB0;   //B-Functions Table

        const uint CYCLES_PER_SECOND = 33868800;
        const uint CYCLES_PER_FRAME = CYCLES_PER_SECOND / 60;

        double CyclesDone = 0;

        public static BUS BUS;
        public static GTE GTE;

        private const uint BIOS_BLOCK_COUNT = BIOS.SIZE >> 2;
        private const uint RAM_BLOCK_COUNT = RAM.SIZE >> 2;
        private readonly uint BIOS_CacheSize = (uint)(BIOS_BLOCK_COUNT * sizeof(x64CacheBlock));
        private readonly uint RAM_CacheSize = (uint)(RAM_BLOCK_COUNT * sizeof(x64CacheBlock));
        private readonly uint CPU_StructSize = (uint)sizeof(CPUNativeStruct);

        bool IsBIOSBlock => (CPU_Struct_Ptr->PC & 0x1FFFFFFF) >= BIOS_START;

        public static CPUNativeStruct* CPU_Struct_Ptr;
        private static x64CacheBlock* BIOS_CacheBlocks;
        private static x64CacheBlock* RAM_CacheBlocks;
        x64CacheBlock* CurrentCache => IsBIOSBlock ? BIOS_CacheBlocks : RAM_CacheBlocks;


        //Stub block to call recompiler
        public static delegate* unmanaged[Stdcall]<void> StubBlockPointer;  

        //This variable probably shouldn't be that high
        private const int MAX_INSTRUCTIONS_PER_BLOCK = 100;

        private static readonly bool ForceLoadDelaySlotEmulation = false;

        static bool IsLoadingEXE;
        static string? EXEPath;
        public uint GetPC() => CPU_Struct_Ptr ->PC;

        private static CPU_x64_Recompiler Instance;

        private CPU_x64_Recompiler(bool isEXE, string? executablePath, BUS bus) {
            BUS = bus;
            GTE = new GTE();
            IsLoadingEXE = isEXE;
            EXEPath = executablePath;
            Reset();
        }

        public static CPU_x64_Recompiler GetCPU(bool isEXE, string? EXEPath, BUS bus) {
            if (Instance == null) {
                Instance = new CPU_x64_Recompiler(isEXE, EXEPath, bus);
            }
            return Instance;
        }

        public void Reset() {
            CyclesDone = 0;
            BIOS_CacheBlocks = (x64CacheBlock*)NativeMemoryManager.AllocateNativeMemory(BIOS_CacheSize);
            RAM_CacheBlocks = (x64CacheBlock*)NativeMemoryManager.AllocateNativeMemory(RAM_CacheSize);
            CPU_Struct_Ptr = (CPUNativeStruct*)NativeMemoryManager.AllocateNativeMemory(CPU_StructSize);
            StubBlockPointer = LinkStubBlock(x64_JIT.EmitStubBlock());
            AllocateExecutableMemory();

            CPU_Struct_Ptr->PC = RESET_VECTOR;
            CPU_Struct_Ptr->Next_PC = RESET_VECTOR + 4;
            CPU_Struct_Ptr->HI = 0xDeadBeef;
            CPU_Struct_Ptr->LO = 0xDeadBeef;

            //Initialize JIT cache for BIOS region
            for (int i = 0; i < BIOS_BLOCK_COUNT; i++) {
                BIOS_CacheBlocks[i].FunctionPointer = StubBlockPointer;
            }

            //Initialize JIT cache for RAM region
            for (int i = 0; i < RAM_BLOCK_COUNT; i++) {
                RAM_CacheBlocks[i].FunctionPointer = StubBlockPointer;
            }

            //Scheduler is static, make sure to clear it when resetting
            Scheduler.FlushAllEvents();

            //Schedule 1 initial SPU event
            Scheduler.ScheduleInitialEvent((int)SPU.CYCLES_PER_SAMPLE, BUS.SPU.SPUCallback, Event.SPU);

            //Schedule 1 initial vblank event
            Scheduler.ScheduleInitialEvent((int)CYCLES_PER_FRAME, BUS.GPU.VblankEventCallback, Event.Vblank);
        }

        public void TickFrame() {
            ulong currentTime = CPU_Struct_Ptr->CurrentCycle;
            ulong endFrameTime = currentTime + CYCLES_PER_FRAME;

            while (currentTime < endFrameTime) {         
                //Run the CPU until the next event
                while (CPU_Struct_Ptr->CurrentCycle < Scheduler.ScheduledEvents[0].EndTime) {
                    Run();
                }

                ScheduledEvent readyEvent = Scheduler.ScheduledEvents[0];
                Scheduler.ScheduledEvents.RemoveAt(0);

                //Handle the ready event and check for interrupts
                //TODO: Handle GTE IRQ behaviour
                readyEvent.Callback();
                IRQCheck();  

                //Update current time
                currentTime = CPU_Struct_Ptr->CurrentCycle;
            }

            CyclesDone += CYCLES_PER_FRAME;
        }

        public void Run() {
            //Console.WriteLine($"Running {CPU_Struct_Ptr->PC:X8}");
            uint block = GetBlockAddress(CPU_Struct_Ptr->PC, IsBIOSBlock);
            CurrentCache[block].FunctionPointer();
        }      

       public static ReadOnlySpan<uint> GetInstructionMemory(uint address) {        
            address &= 0x1FFFFFFF;
            bool isBios = address >= BIOS_START;
            int offset = (int)address;
            byte* start;
            int size;

            if (isBios) {
                offset -= (int)BIOS_START;
                start = BUS.BIOS.NativeAddress;
                size = (int)BIOS.SIZE;
            } else {
                start = BUS.RAM.NativeAddress;
                size = (int)RAM.SIZE;
            }

            ReadOnlySpan<byte> rawMemory = new ReadOnlySpan<byte>(start, size).Slice(offset);
            return MemoryMarshal.Cast<byte, uint>(rawMemory);
        }

        public void SetInvalidAllRAMBlocks() {
            //On FlushCache invalidate all ram blocks
            for (int i = 0; i < RAM_BLOCK_COUNT; i++) {
                RAM_CacheBlocks[i].FunctionPointer = StubBlockPointer;
            }
            //Console.WriteLine("RAM Flushed");
        }

        public void SetInvalidAllBIOSBlocks() {
            //BIOS is only flushed when we're out of memory
            for (int i = 0; i < BIOS_BLOCK_COUNT; i++) {
                BIOS_CacheBlocks[i].FunctionPointer = StubBlockPointer;
            }
            //Console.WriteLine("BIOS Flushed");
        }

        public void SetInvalidRAMBlock(uint block) {
            //Hacky way to set blocks as invalid upon RAM writes
            RAM_CacheBlocks[block].FunctionPointer = StubBlockPointer;

            //This could still not work if the game patches the 
            //middle of a function then jumps to the beginning
        }

        public static void SetupJITBlock(Assembler emitter, x64CacheBlock* block) {
            x64_JIT.IsFirstInstruction = true;

            x64_JIT.CompileTime_CurrentPC = block->Address;
            x64_JIT.CompileTime_PC = block->Address;
            x64_JIT.CompileTime_NextPC = block->Address + 4;
            x64_JIT.EmitBlockEntry(emitter);

            //Emit TTY Handlers on these addresses
            if (block->Address == A_FunctionsTableAddress ||
                block->Address == B_FunctionsTableAddress) {
                x64_JIT.EmitTTY(emitter, block->Address);
            }
        }

        public static void UpdateCompileTimePC() {
            x64_JIT.CompileTime_CurrentPC = x64_JIT.CompileTime_PC;
            x64_JIT.CompileTime_PC = x64_JIT.CompileTime_NextPC;
            x64_JIT.CompileTime_NextPC += 4;
        }

        private static void Recompile(x64CacheBlock* cacheBlock, uint cyclesPerInstruction) {
            //Console.WriteLine($"Compiling: {cacheBlock->Address:X8}");
            Instruction instruction = new Instruction();
            Assembler emitter = new Assembler(64);
            Label endOfBlock = emitter.CreateLabel();
            bool inDelaySlot = false;
            int instructionIndex = 0;
            int loadDelayCounter = 0;
            ReadOnlySpan<uint> instructionsSpan = GetInstructionMemory(cacheBlock->Address);
            SetupJITBlock(emitter, cacheBlock);

            //Whenever we are emitting a branch, we must write the compile time PC variables into the runtime PC variables
            //we also need to make sure to emit the branch delay handler after it

            for (;;) {
                UpdateCompileTimePC();
                instruction.Value = instructionsSpan[instructionIndex++];
                bool reachedMaxInstructions = instructionIndex > MAX_INSTRUCTIONS_PER_BLOCK;
                bool isSyscallOrBreak = IsSyscallOrBreak(instruction);
                bool isJumpOrBranch = IsJumpOrBranch(instruction);

                //End the block if any of these conditions is true
                bool endBlock = isSyscallOrBreak || inDelaySlot || reachedMaxInstructions;

                //Update runtime PC variables if any of these conditions is true, unless we're in a delay slot (which means they are up to date)
                bool updateRuntimePC = (isSyscallOrBreak || isJumpOrBranch || reachedMaxInstructions) && !inDelaySlot;   

                if (loadDelayCounter <= 0) {
                    //Experimental -- There might be edge cases that are not covered
                    loadDelayCounter = NeedsDelaySlot(instruction, instructionsSpan, instructionIndex, inDelaySlot);
                }

                if (updateRuntimePC) {
                    //Update Runtime PC variables, unless this IS a delay slot
                    //(in that case it's up to date, and we should not overwrite)
                    x64_JIT.EmitWUpdateRuntimePC(emitter);
                }

                EmitInstruction(instruction, emitter, cyclesPerInstruction, loadDelayCounter--);

                if (endBlock) {
                    cacheBlock->TotalCycles = (uint)(instructionIndex * cyclesPerInstruction);
                    x64_JIT.TerminateBlock(emitter, ref endOfBlock);
                    AssembleAndLink(emitter, ref endOfBlock, cacheBlock);
                    return;
                }

                //For jumps and branches, we set the flag such that the delay slot is also included
                inDelaySlot = isJumpOrBranch;

                if (inDelaySlot) {
                    //Update runtime PC if we executed a jump/branch, so that
                    //the delay slot will see updated values, as well as the recompiler when we exit the block
                    x64_JIT.EmitBranchDelayHandler(emitter);
                }
            }
        }

        public static void EmitInstruction(Instruction instruction, Assembler emitter, uint cyclesPerInstruction, int loadDelayCounter) {
            x64_JIT.EnableLoadDelaySlot = (loadDelayCounter > 0) || ForceLoadDelaySlotEmulation;

            //Emit branch delay to keep PC registers up to date
            //x64_JIT.EmitBranchDelayHandler(emitter);

            //Emit the actual instruction (we don't emit NOPs)
            if (instruction.Value != 0) {
                x64_LUT.MainLookUpTable[instruction.Op](instruction, emitter);
            }

            if (x64_JIT.EnableLoadDelaySlot) {
                //Emit the load delay handling
                x64_JIT.EmitRegisterTransfare(emitter);
            } else {
                if (x64_JIT.IsFirstInstruction) {
                    //Handle possible delayed load from previous block
                    Span<uint> regs = Register_LUT.MainLookUpTable[instruction.Op](instruction);
                    x64_JIT.MaybeCancelLoadDelay(emitter, (int)regs[0]);
                    x64_JIT.IsFirstInstruction = false;
                }
            }

            //Update the current cycle 
            x64_JIT.EmitUpdateCurrentCycle(emitter, (int)cyclesPerInstruction);
        }

        private static uint GetBlockAddress(uint address, bool biosBlock) {
            address &= 0x1FFFFFFF;
            if (biosBlock) {
                address -= BIOS_START;
            } else {
                address &= ((1 << 21) - 1); // % 2MB 
            }
            return address >> 2;
        }

        private static uint GetJumpTarget(uint PC, Instruction instruction) {
            uint target = (PC & 0xf0000000) | (instruction.JumpImm << 2);
            return target;
        }

        private static bool IsJumpOrBranch(Instruction instruction) {
            uint op = instruction.Op;
            if (op == 0) {
                uint sub = instruction.Sub;
                return sub == 0x8 || sub == 0x9;     //JR, JALR,
            } else {
                return op >= 1 && op <= 7;            //BXX, J, JAL, BEQ, BNE, BLEZ, BGTZ 
            }
        }

        private static bool IsSyscallOrBreak(Instruction instruction) {
            uint op = instruction.Op;
            if (op == 0) {
                uint sub = instruction.Sub;
                return sub == 0xC || sub == 0xD;     //Syscall, Break
            }
            return false;
        }

        private static bool IsAnyLoad(Instruction instruction) {
            return (instruction.Op >= 0x20 && instruction.Op <= 0x26) || IsCOPLoad(instruction);
        }
    
        private static bool IsCOPLoad(Instruction instruction) {
            return (instruction.Op == 0x10 && (instruction.Rs == 0)) ||                              //MFC0
                (instruction.Op == 0x12 && (instruction.Rs == 0 || instruction.Rs == 2));           //MFC2/CFC2
        }

        private static bool HasReadDependancy(Span<uint> loadInstructionRegs, Span<uint> nextInstructionRegs) {
            for (int i = 1; i < nextInstructionRegs.Length; i++) {
                if (nextInstructionRegs[i] == loadInstructionRegs[0]) {
                    return true;
                }
            }
            return false;
        }

        private static int NeedsDelaySlot(Instruction instruction, ReadOnlySpan<uint> instructions, int index, bool end) {         
            //If the instruction is not any form of load
            if (!IsAnyLoad(instruction)) {
                return 0;
            }

            //Always delay the load if it's in the branch delay slot
            //The MaybeCancelLoadDelay function should figure out what to do
            if (end) {
                return 1; 
            }

            Instruction nextInstruction = new Instruction {
                Value = instructions[index]
            };

            Instruction nextNextInstruction = new Instruction {
                Value = instructions[index + 1]
            };

            Span<uint> regsOfCurrentInstruction = Register_LUT.MainLookUpTable[instruction.Op](instruction);
            Span<uint> regsOfNextInstruction = Register_LUT.MainLookUpTable[nextInstruction.Op](nextInstruction);
            Span<uint> regsOfNextNextInstruction = Register_LUT.MainLookUpTable[nextNextInstruction.Op](nextNextInstruction);

            if (regsOfCurrentInstruction[0] == 0) {
                return 0;
            }

            //Special Cases:
            //LW then LW then Read
            //LWL/R then LWL/R then Read
            //Basically any consecutive loads followed by a read directly
            //TODO: Handle N loads followed by a read 
            if ((IsAnyLoad(nextInstruction) && regsOfNextInstruction[0] == regsOfCurrentInstruction[0])) {            
                //Check the if 3rd instruction reads from the load target
                if (HasReadDependancy(regsOfCurrentInstruction, regsOfNextNextInstruction)) {
                    return 3;
                }

                if (HasReadDependancy(regsOfCurrentInstruction, regsOfNextInstruction)) {
                    return 2;
                }

                return 0;
            }

            //Normal Case where a load followed by a normal instruction
            //Check if that instruction reads from the load target
            if (HasReadDependancy(regsOfCurrentInstruction, regsOfNextInstruction)) {
                return 2;
            }

            return 0;
        }

        private static void IRQCheck() {
            if (IRQ_CONTROL.isRequestingIRQ()) {  //Interrupt check    
                CPU_Struct_Ptr->COP0_Cause |= (1 << 10);
                uint sr = CPU_Struct_Ptr->COP0_SR;

                //TODO: Skip IRQs if the current instruction is a GTE instruction to avoid the BIOS skipping it
                if (((sr & 1) != 0) && (((sr >> 10) & 1) != 0)) {
                    Exception(CPU_Struct_Ptr, (uint)CPU.Exceptions.IRQ);
                }
            }
        }

        public static void Exception(CPUNativeStruct* cpuStruct, uint exceptionCause) {
            //If the next instruction is a GTE instruction skip the exception
            //Otherwise the BIOS will try to handle the GTE bug by skipping the instruction  
            uint handler;                                         //Get the handler

            if ((cpuStruct->COP0_SR & (1 << 22)) != 0) {
                handler = 0xbfc00180;
            } else {
                handler = 0x80000080;
            }

            uint mode = cpuStruct->COP0_SR & 0x3f;                     //Disable interrupts 

            cpuStruct->COP0_SR = (uint)(cpuStruct->COP0_SR & ~0x3f);
            cpuStruct->COP0_SR = cpuStruct->COP0_SR | ((mode << 2) & 0x3f);
            cpuStruct->COP0_Cause = exceptionCause << 2;               //Update cause register

            //Small hack: if IRQ happens step the branch delay to avoid having the handler pointing 
            //to the previous instruction which is the delay slot instruction
            //Note: when we leave JIT we are (almost always) in a delay slot
            if (exceptionCause == (int)CPU.Exceptions.IRQ) {
                cpuStruct->COP0_EPC = cpuStruct->PC;           //Save the PC in register EPC (we might fall into a GTE instruction here!)
                cpuStruct->DelaySlot = cpuStruct->Branch;
                cpuStruct->Branch = 0;
            } else {
                cpuStruct->COP0_EPC = cpuStruct->Current_PC;   //Save the current PC in register EPC
            }

            if (cpuStruct->DelaySlot == 1) {                            //In case an exception occurs in a delay slot
                cpuStruct->COP0_EPC -= 4;
                cpuStruct->COP0_Cause = (uint)(cpuStruct->COP0_Cause | (1 << 31));
            }

            cpuStruct->PC = handler;                                   //Jump to the handler address (no delay)
            cpuStruct->Next_PC = cpuStruct->PC + 4;
        }

        public ref BUS GetBUS() {
            return ref BUS;
        }

        private static void LoadTestRom(string? path) {
            byte[] EXE = File.ReadAllBytes(path);

            //Copy the EXE data to memory
            uint addressInRAM = (uint)(EXE[0x018] | (EXE[0x018 + 1] << 8) | (EXE[0x018 + 2] << 16) | (EXE[0x018 + 3] << 24));

            for (int i = 0x800; i < EXE.Length; i++) {
                BUS.WriteByte(addressInRAM, EXE[i]);
                addressInRAM++;
            }

            //Set up SP, FP, and GP
            uint baseStackAndFrameAddress = (uint)(EXE[0x30] | (EXE[0x30 + 1] << 8) | (EXE[0x30 + 2] << 16) | (EXE[0x30 + 3] << 24));

            if (baseStackAndFrameAddress != 0) {
                uint stackAndFrameOffset = (uint)(EXE[0x34] | (EXE[0x34 + 1] << 8) | (EXE[0x34 + 2] << 16) | (EXE[0x34 + 3] << 24));
                CPU_Struct_Ptr->GPR[(int)CPU.Register.sp] = CPU_Struct_Ptr->GPR[(int)CPU.Register.fp] = baseStackAndFrameAddress + stackAndFrameOffset;
            }

            CPU_Struct_Ptr->GPR[(int)CPU.Register.gp] = (uint)(EXE[0x14] | (EXE[0x14 + 1] << 8) | (EXE[0x14 + 2] << 16) | (EXE[0x14 + 3] << 24));

            //Jump to the address specified by the EXE
            CPU_Struct_Ptr->Current_PC = CPU_Struct_Ptr->PC = (uint)(EXE[0x10] | (EXE[0x10 + 1] << 8) | (EXE[0x10 + 2] << 16) | (EXE[0x10 + 3] << 24));
            CPU_Struct_Ptr->Next_PC = CPU_Struct_Ptr->PC + 4;
        }

        //Sampled every second by timer
        public double GetSpeed() {
            double returnValue = (CyclesDone / CYCLES_PER_SECOND) * 100;
            CyclesDone = 0;
            return returnValue;
        }

        public ulong GetCurrentCycle() {
            return CPU_Struct_Ptr->CurrentCycle;
        }

        //Unused
        private void TTY(uint pc) {

            switch (pc) {
                case 0xA0:      //Intercepting prints to the TTY Console and printing it in console 
                    char character;

                    switch (CPU_Struct_Ptr->GPR[9]) {
                        case 0x3C:                       //putchar function (Prints the char in $a0)
                            character = (char)CPU_Struct_Ptr->GPR[4];
                            Console.Write(character);
                            break;

                        case 0x3E:                        //puts function, similar to printf but differ in dealing with 0 character
                            uint address = CPU_Struct_Ptr->GPR[4];       //address of the string is in $a0
                            if (address == 0) {
                                Console.Write("\\<NULL>");
                            } else {
                                while (BUS.ReadByte(address) != 0) {
                                    character = (char)BUS.ReadByte(address);
                                    Console.Write(character);
                                    address++;
                                }

                            }
                            break;
                    }
                    break;

                case 0xB0:
                    switch (CPU_Struct_Ptr->GPR[9]) {
                        case 0x3D:                       //putchar function (Prints the char in $a0)
                            character = (char)CPU_Struct_Ptr->GPR[4];
                            Console.Write(character);
                            break;

                        case 0x3F:                                       //puts function, similar to printf but differ in dealing with 0 character
                            uint address = CPU_Struct_Ptr->GPR[4];       //address of the string is in $a0
                            if (address == 0) {
                                Console.Write("\\<NULL>");
                            } else {
                                while (BUS.ReadByte(address) != 0) {
                                    character = (char)BUS.ReadByte(address);
                                    Console.Write(character);
                                    address++;
                                }
                            }
                            break;
                    }
                    break;
            }
        }

        //Unused
        private static bool InstructionIsGTE(CPU_x64_Recompiler cpu) {
            return false; //Does not work with current JIT
        }
      
        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing) {
            if (!Disposed) {
                if (disposing) {
                    //Free managed objects
                    //Unlink everything

                    for (int i = 0; i < BIOS_BLOCK_COUNT; i++) {
                        BIOS_CacheBlocks[i].FunctionPointer = null;
                    }

                    for (int i = 0; i < RAM_BLOCK_COUNT; i++) {
                        RAM_CacheBlocks[i].FunctionPointer = null;
                    }

                    GTE = null;
                    Instance = null;
                }

                //Free unmanaged objects (note that the memory manager will call free, since it tracks the allocations)
                CPU_Struct_Ptr = null; 
                StubBlockPointer = null;
                ExecutableMemoryBase = null;
                AddressOfNextBlock = null;
                Disposed = true;
            }
        }

        ~CPU_x64_Recompiler() {
            Dispose(false);
        }
    }
}
