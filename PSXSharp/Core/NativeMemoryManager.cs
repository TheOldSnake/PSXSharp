using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PSXSharp.Core {
    public static unsafe class NativeMemoryManager {
        //Import needed kernel functions
        [DllImport("kernel32.dll")]
        private static extern void* VirtualAlloc(void* addr, int size, int type, int protect);

        [DllImport("kernel32.dll")]
        private static extern bool VirtualProtect(void* addr, int size, int new_protect, int* old_protect);

        [DllImport("kernel32.dll")]
        private static extern bool VirtualFree(void* addr, int size, int type);

        [DllImport("kernel32.dll")]
        private static extern bool FlushInstructionCache(IntPtr hProcess, IntPtr lpBaseAddress, UIntPtr dwSize);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        public static IntPtr ProcessHandle = GetCurrentProcess();

        private const int PAGE_EXECUTE_READWRITE = 0x40;
        private const int MEM_COMMIT = 0x00001000;
        private const int MEM_RELEASE = 0x00008000;

        //Lists of memory allocations, that we only need to free them on reset/exit
        private static readonly List<nuint> Allocations = [];
        private static readonly List<ExecutableMemory> ExecutableAllocations = [];

        public struct ExecutableMemory { 
            public void* Address;
            public int Size;
        }

        public static void FlushInstructionCache(nint ptr, nuint size) => FlushInstructionCache(ProcessHandle, ptr, size);
        public static void FillNativeMemory(void* ptr, nuint byteCount, byte value) => NativeMemory.Fill(ptr, byteCount, value);
        public static void CopyNativeMemory(void* src, void* dest, nuint byteCount) => NativeMemory.Copy(src, dest, byteCount);

        public static void* AllocateExecutableMemory(int size, bool isTracked = true) {
            //Allocate 64MB of executable memory
            void* memory = VirtualAlloc(null, size, MEM_COMMIT, PAGE_EXECUTE_READWRITE);
            if (isTracked) {
                ExecutableAllocations.Add(new ExecutableMemory { Address = memory, Size = size });
            }
            Console.WriteLine($"[NativeMemoryManager] Allocated 0x{size:X} bytes [Executable]");
            return memory;
        }

        public static void FreeExecutableMemory(ExecutableMemory memory) {
            //Manual free, for allocations that are not tracked in the list
            if (memory.Address == null) {
                return;
            }

            //Just in case it somehow exists in the list, make sure to remove it
            ExecutableAllocations.Remove(memory);

            //Free the memory
            VirtualFree(memory.Address, memory.Size, MEM_RELEASE);
        }

        public static void* AllocateNativeMemory(uint size, bool isTracked = true) {   
            void* memory = NativeMemory.AllocZeroed(size);
            Console.WriteLine($"[NativeMemoryManager] Allocated 0x{size:X} bytes");

            //By default, we track all allocations and free them on exit/reset,
            //if isTracked is set to false, it's the callers responsibility to call FreeNativeMemory()
            if (isTracked) {
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
            Allocations.Remove((nuint)memory);

            //Free the memory
            NativeMemory.Free(memory);
        }

        public static void ResetMemory() {
            //Free all memory allocations
            foreach (ExecutableMemory executableMemory in ExecutableAllocations) {
                VirtualFree(executableMemory.Address, executableMemory.Size, MEM_RELEASE);
            }

            foreach (void* memory in Allocations) {
                NativeMemory.Free(memory);
            }

            ExecutableAllocations.Clear();
            Allocations.Clear();
            Console.WriteLine("[NativeMemoryManager] Memory Freed!");
        }
    }
}
