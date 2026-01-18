using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PSXSharp.Core.x64_Recompiler {
    public unsafe partial class CPU_x64_Recompiler {
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        public static delegate* unmanaged[Stdcall]<void> StubBlockHandler() {
            //Code to be called in all non compiled blocks

            //If we end up in an invalid address
            if ((CPU_Struct_Ptr->PC & 0x3) != 0) {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[x64 JIT] Invalid PC!");
                Console.ForegroundColor = ConsoleColor.Green;
                throw new Exception();
            }

            //If we need to load an EXE, this should happen here because 
            //the LoadTestRom will change the PC 
            if (CPU_Struct_Ptr->PC == SHELL_START) {
                if (IsLoadingEXE) {
                    IsLoadingEXE = false;
                    LoadTestRom(EXEPath);
                }
            }

            bool isBios = (CPU_Struct_Ptr->PC & 0x1FFFFFFF) >= BIOS_START;
            uint block = GetBlockAddress(CPU_Struct_Ptr->PC, isBios);
            int maskedAddress = (int)(CPU_Struct_Ptr->PC & 0x1FFFFFFF);

            uint cyclesPerInstruction;
            x64CacheBlock* currentBlock;

            if (isBios) {
                cyclesPerInstruction = 22;
                currentBlock = &BIOS_CacheBlocks[block];
            } else {
                cyclesPerInstruction = 2;
                currentBlock = &RAM_CacheBlocks[block];
            }

            currentBlock->Address = CPU_Struct_Ptr->PC;
            Recompile(currentBlock, cyclesPerInstruction);

            //After compilation we need to clear our actual CPU cache for that address
            NativeMemoryManager.FlushInstructionCache((nint)currentBlock->FunctionPointer, (nuint)currentBlock->SizeOfAllocatedBytes);

            //Console.WriteLine("Running after compilation " + CPU_Struct_Ptr->PC.ToString("x"));
            //Return the address to be called in asm
            return currentBlock->FunctionPointer;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        public static void TTYA0Handler() {
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
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        public static void TTYB0Handler() {
            char character;
            switch (CPU_Struct_Ptr->GPR[9]) {
                case 0x3D:                       //putchar function (Prints the char in $a0)
                    character = (char)CPU_Struct_Ptr->GPR[4];
                    Console.Write(character);
                    break;

                case 0x3F:                        //puts function, similar to printf but differ in dealing with 0 character
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
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        public static byte BUSReadByteWrapper(uint address) => BUS.ReadByte(address);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        public static ushort BUSReadHalfWrapper(uint address) => BUS.ReadHalf(address);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        public static uint BUSReadWordWrapper(uint address) => BUS.ReadWord(address);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        public static void BUSWriteByteWrapper(uint address, byte value) => BUS.WriteByte(address, value);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        public static void BUSWriteHalfWrapper(uint address, ushort value) => BUS.WriteHalf(address, value);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        public static void BUSWriteWordWrapper(uint address, uint value) => BUS.WriteWord(address, value);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        public static uint GTEReadWrapper(uint rd) => GTE.read(rd);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        public static void GTEWriteWrapper(uint rd, uint value) => GTE.write(rd, value);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        public static void GTEExecuteWrapper(uint value) => GTE.execute(value);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        public static void ExceptionWrapper(CPUNativeStruct* cpuStruct, uint cause) => Exception(cpuStruct, cause);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        public static void Print(uint val) => Console.WriteLine("[X64 Debug] " + val.ToString("x"));
    }
}
