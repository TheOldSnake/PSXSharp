using Iced.Intel;
using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Instruction = PSXSharp.Core.Common.Instruction;
using Label = Iced.Intel.Label;

namespace PSXSharp.Core.x64_Recompiler {
    public unsafe partial class CPU_x64_Recompiler : CPU, IDisposable {
      
        [DllImport("kernel32.dll")]
        private static extern bool FlushInstructionCache(IntPtr hProcess, IntPtr lpBaseAddress, UIntPtr dwSize);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        private bool disposed = false;

        public static IntPtr ProcessHandle = GetCurrentProcess();

        public static CPUNativeStruct* CPU_Struct_Ptr;
        public const uint RESET_VECTOR = 0xBFC00000;
        public const uint BIOS_START = 0x1FC00000;          //Reset vector but masked
        public const uint BIOS_SIZE = 512 * 1024;           //512 KB
        public const uint RAM_SIZE = 2 * 1024 * 1024;       //2 MB
        public const uint RAM_SIZE_8MB = RAM_SIZE * 4;    

        const uint CYCLES_PER_SECOND = 33868800;
        const uint CYCLES_PER_FRAME = CYCLES_PER_SECOND / 60;
        const int CYCLES_PER_SPU_SAMPLE = 0x300;

        double CyclesDone = 0;

        bool IsReadingFromBIOS => (CPU_Struct_Ptr->PC & 0x1FFFFFFF) >= BIOS_START;

        public static BUS BUS;
        public static GTE GTE;

        public static x64CacheBlock[] BIOS_CacheBlocks;
        public static x64CacheBlock[] RAM_CacheBlocks;
        public static x64CacheBlock CurrentBlock;
 
        public NativeMemoryManager MemoryManager;
        private static CPU_x64_Recompiler Instance;

        public static delegate* unmanaged[Stdcall]<void> StubBlockPointer;  //Stub block to call recompiler
        private const int MAX_INSTRUCTIONS_PER_BLOCK = 30;

        bool IsLoadingEXE;
        string? EXEPath;

        private CPU_x64_Recompiler(bool isEXE, string? EXEPath, BUS bus) {           
            BUS = bus;
            GTE = new GTE();
            CurrentBlock = new x64CacheBlock();
            MemoryManager = NativeMemoryManager.GetOrCreateMemoryManager();
            IsLoadingEXE = isEXE;
            this.EXEPath = EXEPath;
            Reset();
        }

        public static CPU_x64_Recompiler GetOrCreateCPU(bool isEXE, string? EXEPath, BUS bus) {
            if (Instance == null) {
                Instance = new CPU_x64_Recompiler(isEXE, EXEPath, bus);
            }
            return Instance;
        }

        public void Reset() {
            MemoryManager.Reset();

            CPU_Struct_Ptr = MemoryManager.GetCPUNativeStructPtr();
            StubBlockPointer = MemoryManager.CompileStubBlock();

            CPU_Struct_Ptr->PC = RESET_VECTOR;
            CPU_Struct_Ptr->Next_PC = RESET_VECTOR + 4;
            CPU_Struct_Ptr->HI = 0xDeadBeef;
            CPU_Struct_Ptr->LO = 0xDeadBeef;

            //Initialize JIT cache for BIOS region
            BIOS_CacheBlocks = new x64CacheBlock[BIOS_SIZE >> 2];
            for (int i = 0; i < BIOS_CacheBlocks.Length; i++) {
                BIOS_CacheBlocks[i] = new x64CacheBlock();
                BIOS_CacheBlocks[i].FunctionPointer = StubBlockPointer;        
            }

            //Initialize JIT cache for RAM region
            RAM_CacheBlocks = new x64CacheBlock[RAM_SIZE >> 2];
            for (int i = 0; i < RAM_CacheBlocks.Length; i++) {
                RAM_CacheBlocks[i] = new x64CacheBlock();
                RAM_CacheBlocks[i].FunctionPointer = StubBlockPointer;
            }

            //Scheduler is static, make sure to clear it when resetting
            Scheduler.FlushAllEvents();

            //Schedule 1 initial SPU event
            Scheduler.ScheduleInitialEvent(CYCLES_PER_SPU_SAMPLE, BUS.SPU.SPUCallback, Event.SPU);

            //Schedule 1 initial vblank event
            Scheduler.ScheduleInitialEvent((int)CYCLES_PER_FRAME, BUS.GPU.VblankEventCallback, Event.Vblank);
        }

        public void TickFrame() {
            ulong currentTime = CPU_Struct_Ptr->CurrentCycle;
            ulong endFrameTime = currentTime + CYCLES_PER_FRAME;

            while (currentTime < endFrameTime) {
                //Get the next event
                ScheduledEvent nextEvent = Scheduler.DequeueNearestEvent();

                //Run the CPU until the event
                while (CPU_Struct_Ptr->CurrentCycle < nextEvent.EndTime) {
                    Run();                    
                }

                //Handle the ready event and check for interrupts
                nextEvent.Callback();
                IRQCheck();

                //Update current time
                currentTime = CPU_Struct_Ptr->CurrentCycle;
            }
            
            CyclesDone += CYCLES_PER_FRAME;
        }

        public void Run() {
            /*if (CPU_Struct_Ptr->PC == 0x80030000) {
                if (IsLoadingEXE) {
                    IsLoadingEXE = false;
                    loadTestRom(EXEPath);                   
                }
            }

            TTY(CPU_Struct_Ptr->PC);*/

            bool isBios = (CPU_Struct_Ptr->PC & 0x1FFFFFFF) >= BIOS_START;
            uint block = GetBlockAddress(CPU_Struct_Ptr->PC, isBios);
            x64CacheBlock[] currentCache = isBios ? BIOS_CacheBlocks : RAM_CacheBlocks;
            RunJIT(currentCache[block]);
        }

        private void RunJIT(x64CacheBlock block) {
            block.FunctionPointer();
            //int totalCycles = (int)(block.TotalCycles);
            //return totalCycles;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        public static ulong StubBlockHandler() {
            //Code to be called in all non compiled blocks
            bool isBios = (CPU_Struct_Ptr->PC & 0x1FFFFFFF) >= BIOS_START;
            uint block = GetBlockAddress(CPU_Struct_Ptr->PC, isBios);
            x64CacheBlock[] currentCache = isBios ? BIOS_CacheBlocks : RAM_CacheBlocks;

            Recompile(block, CPU_Struct_Ptr->PC, isBios);

            //After compilation we need to clear our actual CPU cache for that address
            FlushInstructionCache(ProcessHandle, (nint)currentCache[block].FunctionPointer, (nuint)currentCache[block].SizeOfAllocatedBytes);
            //Return the address to be called in asm
            return (ulong)currentCache[block].FunctionPointer;  
        }

        public void SetInvalidAllRAMBlocks() {
            //On FlushCache invalidate all ram blocks
            Parallel.For(0, RAM_CacheBlocks.Length, i => {
                RAM_CacheBlocks[i].FunctionPointer = StubBlockPointer;
            });
            //Console.WriteLine("RAM Flushed");
        }

        public void SetInvalidAllBIOSBlocks() {
            //BIOS is only flushed when we're out of memory
            Parallel.For(0, BIOS_CacheBlocks.Length, i => {
                BIOS_CacheBlocks[i].FunctionPointer = StubBlockPointer;
            });
            //Console.WriteLine("BIOS Flushed");
        }

        public void SetInvalidRAMBlock(uint block) {
            //Hacky way to set blocks as invalid upon RAM writes
            RAM_CacheBlocks[block].FunctionPointer = StubBlockPointer;

            //This could still not work if the game patches the 
            //middle of a function then jumps to the beginning
        }

        private static void Recompile(uint block, uint pc, bool isBios) {
            Instruction instruction = new Instruction();
            Assembler emitter = new Assembler(64);
            Label endOfBlock = emitter.CreateLabel();
            ReadOnlySpan<byte> rawMemory;
            x64CacheBlock[] currentCache;
            uint cyclesPerInstruction;
            int maskedAddress = (int)(pc & 0x1FFFFFFF);
            bool end = false;

            if (isBios) {
                rawMemory = new ReadOnlySpan<byte>(BUS.BIOS.GetMemoryReference()).Slice((int)(maskedAddress - BIOS_START));
                currentCache = BIOS_CacheBlocks;
                cyclesPerInstruction = 22;
            } else {
                rawMemory = new ReadOnlySpan<byte>(BUS.RAM.GetMemoryPointer(), (int)RAM_SIZE).Slice(maskedAddress);
                currentCache = RAM_CacheBlocks;
                cyclesPerInstruction = 2;
            }

            ReadOnlySpan<uint> instructionsSpan = MemoryMarshal.Cast<byte, uint>(rawMemory);

            CurrentBlock = currentCache[block];
            CurrentBlock.Address = pc;
            CurrentBlock.TotalMIPS_Instructions = 0;
            CurrentBlock.MIPS_Checksum = 0;

            int instructionIndex = 0;

            x64_JIT.EmitBlockEntry(emitter);

            for (;;) {
                instruction.FullValue = instructionsSpan[instructionIndex++];
                bool syscallOrBreak = IsSyscallOrBreak(instruction);

                if (syscallOrBreak) {
                    x64_JIT.EmitSavePC(emitter);  //Needed for the exception procedure
                }

                EmitInstruction(instruction, emitter, cyclesPerInstruction);

                //We end the block if any of these conditions is true
                //Note that syscall and break are immediate exceptions and they don't have delay slot

                if (end || CurrentBlock.TotalMIPS_Instructions > MAX_INSTRUCTIONS_PER_BLOCK || syscallOrBreak) {
                    CurrentBlock.TotalMIPS_Instructions = (uint)instructionIndex;
                    CurrentBlock.TotalCycles = CurrentBlock.TotalMIPS_Instructions * cyclesPerInstruction;                  
                    x64_JIT.TerminateBlock(emitter, ref endOfBlock);
                    AssembleAndLinkPointer(emitter, ref endOfBlock, ref CurrentBlock);
                    currentCache[block] = CurrentBlock;
                    //Console.WriteLine("Compiled: " + pc.ToString("x") + " - " + CurrentBlock.TotalMIPS_Instructions + " Instructions");
                    return;
                }

                //For jumps and branches, we set the flag such that the delay slot is also included
                end = IsJumpOrBranch(instruction);
            }
        }        

        public static void EmitInstruction(Instruction instruction, Assembler emitter, uint cyclesPerInstruction) {
            //Emit branch delay to keep PC registers up to date
            x64_JIT.EmitBranchDelayHandler(emitter);

            //Emit the actual instruction (we don't emit NOPs)
            if (instruction.FullValue != 0) {
                x64_LUT.MainLookUpTable[instruction.GetOpcode()](instruction, emitter);
            }

            //Emit the load delay handling
            x64_JIT.EmitRegisterTransfare(emitter);

            //Update the current cycle 
            x64_JIT.EmitUpdateCurrentCycle(emitter, (int)cyclesPerInstruction);
        }

        public static void AssembleAndLinkPointer(Assembler emitter, ref Label endOfBlockLabel, ref x64CacheBlock block) {
            MemoryStream stream = new MemoryStream();
            AssemblerResult result = emitter.Assemble(new StreamCodeWriter(stream), 0, BlockEncoderOptions.ReturnNewInstructionOffsets);

            //Trim the extra zeroes and the padding in the block by including only up to the ret instruction
            //This works as long as there is no call instruction with the address being passed as 64 bit immediate
            //Otherwise, the address will be inserted at the end of the block and we need to include it in the span
            int endOfBlockIndex = (int)result.GetLabelRIP(endOfBlockLabel);
            Span<byte> emittedCode = new Span<byte>(stream.GetBuffer()).Slice(0, endOfBlockIndex);

            //Pass the old pointer and size. We need them for best fit allocation of next blocks
            NativeMemoryManager manager = NativeMemoryManager.GetOrCreateMemoryManager();           //Get the instance, or make the instance static
            block.FunctionPointer = manager.WriteExecutableBlock(ref emittedCode);
            block.SizeOfAllocatedBytes = emittedCode.Length;      //Update the size to the new one
            block.IsCompiled = true;
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

        private bool IsValidRAMBlock(uint block) {  //For RAM Blocks only
            uint address = BUS.Mask(RAM_CacheBlocks[block].Address);
            uint numberOfInstructions = RAM_CacheBlocks[block].TotalMIPS_Instructions;
            ReadOnlySpan<byte> rawMemory = new ReadOnlySpan<byte>(BUS.RAM.GetMemoryPointer(), (int)RAM_SIZE).Slice((int)address, (int)(numberOfInstructions * 4));
            ReadOnlySpan<uint> instructionsSpan = MemoryMarshal.Cast<byte, uint> (rawMemory);

            uint memoryChecksum = 0;

            for (int i = 0; i < instructionsSpan.Length; i++) {
                memoryChecksum += instructionsSpan[i];
            }

            return RAM_CacheBlocks[block].MIPS_Checksum == memoryChecksum;
        }

        private static bool IsJumpOrBranch(Instruction instruction) {
            uint op = instruction.GetOpcode();
            if (op == 0) {
                uint sub = instruction.Get_Subfunction();
                return sub == 0x8 || sub == 0x9;     //JR, JALR,
            } else {
                return op >= 1 && op <= 7;            //BXX, J, JAL, BEQ, BNE, BLEZ, BGTZ 
            }
        }

        private static bool IsSyscallOrBreak(Instruction instruction) {
            uint op = instruction.GetOpcode();
            if (op == 0) {
                uint sub = instruction.Get_Subfunction();
                return sub == 0xC || sub == 0xD;     //Syscall, Break
            }
            return false;
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
                cpuStruct->COP0_EPC = cpuStruct->PC;           //Save the PC in register EPC
                cpuStruct->DelaySlot = cpuStruct->Branch;
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

        private void loadTestRom(string? path) {
            byte[] EXE = File.ReadAllBytes(path);

            //Copy the EXE data to memory
            uint addressInRAM = (uint)(EXE[0x018] | (EXE[0x018 + 1] << 8) | (EXE[0x018 + 2] << 16) | (EXE[0x018 + 3] << 24));

            for (int i = 0x800; i < EXE.Length; i++) {
                BUS.StoreByte(addressInRAM, EXE[i]);
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
                                while (BUS.LoadByte(address) != 0) {
                                    character = (char)BUS.LoadByte(address);
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
                                while (BUS.LoadByte(address) != 0) {
                                    character = (char)BUS.LoadByte(address);
                                    Console.Write(character);
                                    address++;
                                }
                            }
                            break;
                    }
                    break;
            }
        }

        private static bool InstructionIsGTE(CPU_x64_Recompiler cpu) {
            return false; //Does not work with current JIT
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        public static byte BUSReadByteWrapper(uint address) {
            return BUS.LoadByte(address);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        public static ushort BUSReadHalfWrapper(uint address) {
            return BUS.LoadHalf(address);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        public static uint BUSReadWordWrapper(uint address) {
            return BUS.LoadWord(address);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        public static void BUSWriteByteWrapper(uint address, byte value) {
            BUS.StoreByte(address, value);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        public static void BUSWriteHalfWrapper(uint address, ushort value) {
            BUS.StoreHalf(address, value);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        public static void BUSWriteWordWrapper(uint address, uint value) {
            BUS.StoreWord(address, value);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        public static uint GTEReadWrapper(uint rd) {
            return GTE.read(rd);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        public static void GTEWriteWrapper(uint rd, uint value) {
            GTE.write(rd, value);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        public static void GTEExecuteWrapper(uint value) {
            GTE.execute(value);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        public static void ExceptionWrapper(CPUNativeStruct* cpuStruct, uint cause) {
            Exception(cpuStruct, cause);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        public static void Print(uint val) {
            Console.WriteLine("[X64 Debug] " + val.ToString("x"));
        }        

        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing) {
            if (!disposed) {
                if (disposing) {
                    //Free managed objects
                    //Memory manager will handle freeing CPU_Struct_Ptr and the executable memory
                    MemoryManager.Dispose();

                    MemoryManager = null;
                    CurrentBlock.FunctionPointer = null;

                    foreach (x64CacheBlock block in BIOS_CacheBlocks) {
                        block.FunctionPointer = null;
                    }

                    foreach (x64CacheBlock block in RAM_CacheBlocks) {
                        block.FunctionPointer = null;
                    }

                    BIOS_CacheBlocks = null;
                    RAM_CacheBlocks = null;
                    GTE = null;
                    Instance = null;
                }

                //Free unmanaged objects
                CPU_Struct_Ptr = null;              //We should not call NativeMemory.Free() here.
                disposed = true;
            }
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

        ~CPU_x64_Recompiler() {
            Dispose(false);
        }
    }
}
