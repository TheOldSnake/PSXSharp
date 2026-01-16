using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using PSXSharp.Core.x64_Recompiler;

namespace PSXSharp.Core {
    public static unsafe class NativeMemoryManager {
       // private bool disposedValue;     //Needed for disposing pattern

        //Import needed kernel functions
        [DllImport("kernel32.dll")]
        private static extern void* VirtualAlloc(void* addr, int size, int type, int protect);

        [DllImport("kernel32.dll")]
        private static extern bool VirtualProtect(void* addr, int size, int new_protect, int* old_protect);

        [DllImport("kernel32.dll")]
        private static extern bool VirtualFree(void* addr, int size, int type);

        private const int PAGE_EXECUTE_READWRITE = 0x40;
        private const int MEM_COMMIT = 0x00001000;
        private const int MEM_RELEASE = 0x00008000;

        private const int SIZE_OF_EXECUTABLE_MEMORY = 64 * 1024 * 1024; //64MB

        private static byte* ExecutableMemoryBase;
        private static byte* AddressOfNextBlock;

        //Dummy Code to call recompiler
        public static void* StubBlock;
        private static int StubBlockSize;

        //List of all other memory allocations, that we only need to free them on destroy
        private static List<nuint> Allocations = [];

        public static void AllocateExecutableMemory() {
            //Allocate 64MB of executable memory
            ExecutableMemoryBase = (byte*)VirtualAlloc(null, SIZE_OF_EXECUTABLE_MEMORY, MEM_COMMIT, PAGE_EXECUTE_READWRITE);
            AddressOfNextBlock = ExecutableMemoryBase;
            Console.WriteLine($"[NativeMemoryManager] Allocated 0x{SIZE_OF_EXECUTABLE_MEMORY:X} bytes");
        }

        public static void* AllocateNativeMemory(uint size, bool isTraked = true) {   
            void* memory = NativeMemory.AllocZeroed(size);
            Console.WriteLine($"[NativeMemoryManager] Allocated 0x{size:X} bytes");

            //The default is that we track all allocations and free them on exit,
            //if isTracked is set to false, it's the callers responsibility to call FreeNativeMemory()
            if (isTraked) {
                Allocations.Add((nuint)memory);
            }

            return memory;
        }

        public static void FreeNativeMemory(void* memory) {
            //Manual free, for allocations that are not tracked in the list
            if (memory == null) {
                return;
            }

            //Just in case it somehow exists in the list, make sure to remove it
            if (Allocations.Contains((nuint)memory)) {
                Allocations.Remove((nuint)memory);
            }

            NativeMemory.Free(memory);
        }

        public static void FillNativeMemory(void* ptr, nuint count, byte value) {
            NativeMemory.Fill(ptr, count, value);
        }

        public static void CopyNativeMemory(void* src, void* dest, nuint count) {
            NativeMemory.Copy(src, dest, count);
        }

        public static delegate* unmanaged[Stdcall]<void> CompileStubBlock() {
            Span<byte> emittedCode = x64_JIT.EmitStubBlock();
            StubBlockSize = emittedCode.Length;
            StubBlock = VirtualAlloc(null, StubBlockSize, MEM_COMMIT, PAGE_EXECUTE_READWRITE);
            fixed (byte* blockPtr = &emittedCode[0]) {
                NativeMemory.Copy(blockPtr, StubBlock, (nuint)emittedCode.Length);
            }

            return (delegate* unmanaged[Stdcall]<void>)StubBlock;
        }

        public static delegate* unmanaged[Stdcall]<void> WriteExecutableBlock(ref Span<byte> block) {
            delegate* unmanaged[Stdcall] <void> function;
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
                NativeMemory.Copy(blockPtr, AddressOfNextBlock, (nuint)blockLength);
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

        public static bool HasEnoughMemory(int length) {
            //Console.WriteLine(((AddressOfNextBlock + length - ExecutableMemoryBase) / (1024*1024)));
            return SIZE_OF_EXECUTABLE_MEMORY > (AddressOfNextBlock + length - ExecutableMemoryBase);
        }

        public static void ResetMemory() {
            //Free everything
            if (ExecutableMemoryBase != null) {
                VirtualFree(ExecutableMemoryBase, SIZE_OF_EXECUTABLE_MEMORY, MEM_RELEASE);
            }

            if (StubBlock != null) {
                VirtualFree(StubBlock, StubBlockSize, MEM_RELEASE);
            }

            //Free all other memory allocations
            foreach (void* memory in Allocations) {
                NativeMemory.Free(memory);
            }

            Allocations.Clear();
            ExecutableMemoryBase = null;
            AddressOfNextBlock = null;
            StubBlock = null;

            Console.WriteLine("[NativeMemoryManager] Memory Freed!");
        }
    }
}
