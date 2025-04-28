using System;
using static PSXSharp.Core.x64_Recompiler.CPU_x64_Recompiler;
using System.Runtime.InteropServices;
using System.Collections.Generic;

namespace PSXSharp.Core.x64_Recompiler {
    public unsafe class NativeMemoryManager : IDisposable {
        private bool disposedValue;     //Needed for disposing pattern

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

        private static byte* GuestMemory;
        private static byte* ExecutableMemoryBase;
        private byte* AddressOfNextBlock;
        private CPUNativeStruct* CPU_Struct_Ptr;
        private x64CacheBlocksStruct* x64CacheBlocksStructs;

        private static NativeMemoryManager Instance;

        //Keeps a list of invalid blocks that were allocated to a new memory
        //to use their old space
        private List<(ulong address, int size)> InvalidBlocks;
        private bool IsInvalidBlocksSorted = true;
        private int InvalidBlocksTotalSize = 0;

        //Precompiled RegisterTransfare Function
        public static void* RegisterTransfare;
        private int RegisterTransfareSize;

        //Function poitner to emitted dispatcher
        public static void* Dispatcher;
        private int DispatcherSize;

        //Dummy Code to call recompiler
        public static void* StubBlock;
        private int StubBlockSize;

        private NativeMemoryManager() {
            //Allocate 64MB of executable memory
            ExecutableMemoryBase = (byte*)VirtualAlloc(null, SIZE_OF_EXECUTABLE_MEMORY, MEM_COMMIT, PAGE_EXECUTE_READWRITE);
            AddressOfNextBlock = ExecutableMemoryBase;

            //Allocate memory for the main cpu struct in the unmanaged heap for the native code to read/write
            CPU_Struct_Ptr = (CPUNativeStruct*)NativeMemory.AllocZeroed((nuint)sizeof(CPUNativeStruct));
            x64CacheBlocksStructs = (x64CacheBlocksStruct*)NativeMemory.AllocZeroed((nuint)sizeof(x64CacheBlocksStruct));

            InvalidBlocks = new List<(ulong address, int size)>();

            Console.WriteLine("[NativeMemoryManager] Memory Allocated");
        }

        public void Reset() {
            NativeMemory.Clear(CPU_Struct_Ptr, (nuint)sizeof(CPUNativeStruct));
            NativeMemory.Clear(x64CacheBlocksStructs, (nuint)sizeof(x64CacheBlocksStruct));
            NativeMemory.Clear(ExecutableMemoryBase, SIZE_OF_EXECUTABLE_MEMORY);
            AddressOfNextBlock = ExecutableMemoryBase;
            InvalidBlocks.Clear();
            Console.WriteLine("[NativeMemoryManager] Memory Cleared");
        }

        public static NativeMemoryManager GetOrCreateMemoryManager() {
            if (Instance == null) {
                Instance = new NativeMemoryManager();
            }
            return Instance;
        }

        public CPUNativeStruct* GetCPUNativeStructPtr() {
            return CPU_Struct_Ptr;
        }

        public x64CacheBlocksStruct* GetCacheBlocksStructPtr() {
            return x64CacheBlocksStructs;
        }

        public static byte* AllocateGuestMemory() {
            GuestMemory = (byte*)NativeMemory.AllocZeroed(CPU_x64_Recompiler.RAM_SIZE);
            return GuestMemory;
        }

        /*public delegate* unmanaged[Stdcall] <void> CompileDispatcher() {
            Span<byte> emittedCode = x64_JIT.EmitDispatcher();
            DispatcherSize = emittedCode.Length;
            Dispatcher = VirtualAlloc(null, DispatcherSize, MEM_COMMIT, PAGE_EXECUTE_READWRITE);
            fixed (byte* blockPtr = &emittedCode[0]) {
                NativeMemory.Copy(blockPtr, Dispatcher, (nuint)emittedCode.Length);
            }

            return (delegate* unmanaged[Stdcall]<void>)Dispatcher;
        }*/

        public delegate* unmanaged[Stdcall]<void> CompileStubBlock() {
            Span<byte> emittedCode = x64_JIT.EmitStubBlock();
            StubBlockSize = emittedCode.Length;
            StubBlock = VirtualAlloc(null, StubBlockSize, MEM_COMMIT, PAGE_EXECUTE_READWRITE);
            fixed (byte* blockPtr = &emittedCode[0]) {
                NativeMemory.Copy(blockPtr, StubBlock, (nuint)emittedCode.Length);
            }

            return (delegate* unmanaged[Stdcall]<void>)StubBlock;
        }

        public delegate* unmanaged[Stdcall]<void> WriteExecutableBlock(ref Span<byte> block) {
            delegate* unmanaged[Stdcall] <void> function;
            int blockLength = block.Length;

            //Ensure that we have enough memory
            if (!HasEnoughMemory(blockLength)) {
                //Easiest solution: nuke Everything and start over
                //No need to call NativeMemory.Clear, we just reset the pointers and unlink the blocks.
                AddressOfNextBlock = ExecutableMemoryBase;

                CPUWrapper.GetCPUInstance().SetInvalidAllRAMBlocks();
                CPUWrapper.GetCPUInstance().SetInvalidAllBIOSBlocks();

                InvalidBlocks.Clear();
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

        private byte* Align(byte* address, ulong bytes) {
            ulong addressValue = (ulong)address;
            return (byte*)((addressValue + (bytes - 1)) & ~(bytes - 1));
        }

        private byte* BestFit(int size) {
            if (InvalidBlocks.Count == 0) {
                return null;
            }

            if (!IsInvalidBlocksSorted) {
                InvalidBlocks.Sort((a, b) => a.size.CompareTo(b.size)); //Sort by size (ascending)
                IsInvalidBlocksSorted = true;
            }

            for (int i = 0; i < InvalidBlocks.Count; i++) {
                if (InvalidBlocks[i].size >= size) {
                    ulong allocatedBlock = InvalidBlocks[i].address;

                    //If the block is larger, split it and keep the remainder
                    if (InvalidBlocks[i].size > size) {
                        InvalidBlocks[i] = (InvalidBlocks[i].address + (uint)size, InvalidBlocks[i].size - size);
                        InvalidBlocksTotalSize -= size;
                    } else {
                        //Exact fit, remove the block from the free list
                        InvalidBlocksTotalSize -= InvalidBlocks[i].size;
                        InvalidBlocks.RemoveAt(i);
                    }

                    return (byte*)allocatedBlock;
                }
            }

            return null;
        }

        public void MarkFree(ulong address, int sizeInBytes) {
            if (address == (ulong)StubBlockPointer || sizeInBytes == 0) {
                //Don't add something that was never compiled
                return;
            }

            //Mark the block as invalid and can be overwritten
            InvalidBlocks.Add((address, sizeInBytes));
            IsInvalidBlocksSorted = false;
            InvalidBlocksTotalSize += sizeInBytes;
        }

        public bool HasEnoughMemory(int length) {
            //Console.WriteLine(((AddressOfNextBlock + length - ExecutableMemoryBase) / (1024*1024)));
            return SIZE_OF_EXECUTABLE_MEMORY > (AddressOfNextBlock + length - ExecutableMemoryBase);
        }

        protected virtual void Dispose(bool disposing) {
            if (!disposedValue) {
                if (disposing) {
                    // TODO: dispose managed state (managed objects)
                    Instance = null;
                    InvalidBlocks.Clear();
                }

                //Free unmanaged resources (unmanaged objects) and override finalizer
                VirtualFree(ExecutableMemoryBase, SIZE_OF_EXECUTABLE_MEMORY, MEM_RELEASE);  
                VirtualFree(RegisterTransfare, RegisterTransfareSize, MEM_RELEASE);
                VirtualFree(Dispatcher, DispatcherSize, MEM_RELEASE);
                VirtualFree(StubBlock, StubBlockSize, MEM_RELEASE);


                NativeMemory.Free(CPU_Struct_Ptr);
                NativeMemory.Free(x64CacheBlocksStructs);
                NativeMemory.Free(GuestMemory);

                ExecutableMemoryBase = null;
                AddressOfNextBlock = null;
                CPU_Struct_Ptr = null;
                x64CacheBlocksStructs = null;
                RegisterTransfare = null;
                GuestMemory = null;
                Dispatcher = null;
                StubBlock = null;

                disposedValue = true;
                Console.WriteLine("[NativeMemoryManager] Memory Freed Successfully!");
            }
        }

         ~NativeMemoryManager() {
             Dispose(false);
         }

        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }



        /*private void UnlinkAllBlocks(x64CacheBlocksStruct* cacheBlocks) {
            x64CacheBlockInternalStruct* bios = &cacheBlocks->BIOS_CacheBlocks[0];
            x64CacheBlockInternalStruct* ram = &cacheBlocks->RAM_CacheBlocks[0];

            uint biosCount = (BIOS_SIZE >> 2);
            uint ramCount = (CPU_x64_Recompiler.RAM_SIZE >> 2);

            for (int i = 0; i < biosCount; i++) {
                if (bios[i].IsCompiled == 1) {
                    bios[i].IsCompiled = 0;
                    bios[i].FunctionPointer = 0;
                }
            }

            for (int i = 0; i < ramCount; i++) {
                if (ram[i].IsCompiled == 1) {
                    ram[i].IsCompiled = 0;
                    ram[i].FunctionPointer = 0;
                }
            }
        }*/

    }
}
