using Iced.Intel;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace PSXSharp.Core.x64_Recompiler {
    public unsafe partial class CPU_x64_Recompiler {
        private const int SIZE_OF_EXECUTABLE_MEMORY = 64 * 1024 * 1024; //64MB
        private static byte* ExecutableMemoryBase;
        private static byte* AddressOfNextBlock;

        public static bool HasEnoughMemory(int length) => SIZE_OF_EXECUTABLE_MEMORY > (AddressOfNextBlock + length - ExecutableMemoryBase);

        public void AllocateExecutableMemory() {
            ExecutableMemoryBase = (byte*)NativeMemoryManager.AllocateExecutableMemory(SIZE_OF_EXECUTABLE_MEMORY);
            AddressOfNextBlock = ExecutableMemoryBase;
        }

        public static delegate* unmanaged[Stdcall]<void> LinkStubBlock(ReadOnlySpan<byte> emittedCode) {
            int size = emittedCode.Length;
            void* address = NativeMemoryManager.AllocateExecutableMemory(size);
            fixed (byte* blockPtr = &emittedCode[0]) {
                NativeMemoryManager.CopyNativeMemory(blockPtr, address, (nuint)size);
            }

            return (delegate* unmanaged[Stdcall]<void>)address;
        }

        public static void AssembleAndLink(Assembler emitter, ref Label endOfBlockLabel, x64CacheBlock* block) {
            MemoryStream stream = new MemoryStream();
            AssemblerResult result = emitter.Assemble(new StreamCodeWriter(stream), 0, BlockEncoderOptions.ReturnNewInstructionOffsets);

            //Trim the extra zeroes and the padding in the block by including only up to the ret instruction
            //This works as long as there is no call instruction with the address being passed as 64 bit immediate
            //Otherwise, the address will be inserted at the end of the block and we need to include it in the span
            int endOfBlockIndex = (int)result.GetLabelRIP(endOfBlockLabel);
            Span<byte> emittedCode = new Span<byte>(stream.GetBuffer()).Slice(0, endOfBlockIndex);

            block->FunctionPointer = WriteExecutableBlock(ref emittedCode);
            block->SizeOfAllocatedBytes = emittedCode.Length;      //Update the size to the new one
        }

        public static delegate* unmanaged[Stdcall]<void> WriteExecutableBlock(ref Span<byte> block) {
            delegate* unmanaged[Stdcall]<void> function;
            int blockLength = block.Length;

            //Ensure that we have enough memory
            if (!HasEnoughMemory(blockLength)) {
                //Easiest solution: nuke Everything and start over
                //No need to call NativeMemory.Clear, we just reset the pointers and unlink the blocks.
                AddressOfNextBlock = ExecutableMemoryBase;

                CPUWrapper.GetCPUInstance().SetInvalidAllRAMBlocks();
                CPUWrapper.GetCPUInstance().SetInvalidAllBIOSBlocks();

                Console.WriteLine("[NativeMemoryManager] Memory Resetted!");
            }

            //Copy code to the next block address
            //Fix the pointer to managed memory
            fixed (byte* blockPtr = &block[0]) {
                NativeMemoryManager.CopyNativeMemory(blockPtr, AddressOfNextBlock, (nuint)blockLength);
            }

            //Cast to delegate*
            function = (delegate* unmanaged[Stdcall]<void>)AddressOfNextBlock;

            //Update the address for the incoming blocks
            AddressOfNextBlock += blockLength;
            AddressOfNextBlock = Align(AddressOfNextBlock, 16);

            return function;
        }

        private static byte* Align(byte* address, ulong bytes) {   
            ulong addressValue = (ulong)address;
            return (byte*)((addressValue + (bytes - 1)) & ~(bytes - 1));
        }
    }
}
