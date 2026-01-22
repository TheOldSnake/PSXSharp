using PSXSharp.Core;
using PSXSharp.Core.x64_Recompiler;
using System;
using System.Runtime.CompilerServices;

namespace PSXSharp {
    public unsafe class RAM {
        public const uint BASE_ADDRESS = 0x00000000;
        public const uint SIZE = 2 * 1024 * 1024;         //2MB RAM can be mirrored to the first 8MB (strangely, enabled by default)
        public const uint MASK = SIZE - 1;             
        public Range Range = new Range(BASE_ADDRESS, SIZE);

        private readonly byte* Data = (byte*)NativeMemoryManager.AllocateNativeMemory(SIZE);
        public byte* NativeAddress => Data;

        public T Read<T>(uint address) where T : unmanaged {
            address &= MASK;
            return Unsafe.Read<T>(Data + address);
        }

        public void Write<T>(uint address, T value) where T : unmanaged {
            address &= MASK;
            Unsafe.Write<T>(Data + address, value);
            CPUWrapper.GetCPUInstance().SetInvalidRAMBlock(address >> 2);
        }
    }
}
