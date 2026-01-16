using PSXSharp.Core;
using PSXSharp.Core.x64_Recompiler;
using System;
using System.Runtime.CompilerServices;

namespace PSXSharp {
    public unsafe class RAM {
        public const uint BASE_ADDRESS = 0x00000000;
        public const uint SIZE = 2 * 1024 * 1024;         //2MB RAM can be mirrored to the first 8MB (strangely, enabled by default)
        public Range Range = new Range(BASE_ADDRESS, SIZE);

        private readonly byte* Data = (byte*)NativeMemoryManager.AllocateNativeMemory(SIZE);
        public byte* NativeAddress => Data;

        public T Read<T>(uint address) where T : unmanaged {
            uint offset = address - Range.start;
            uint final = Mirror(offset);
            return Unsafe.Read<T>(Data + final);
        }

        public void Write<T>(uint address, T value) where T : unmanaged {
            uint offset = address - Range.start;
            uint final = Mirror(offset);
            Unsafe.Write<T>(Data + final, value);
            CPUWrapper.GetCPUInstance().SetInvalidRAMBlock(final >> 2);
        }

        public uint Mirror(uint address) {
            //Handle memory mirror, but without %  
            //x % (2^n) is equal to x & ((2^n)-1)
            //So x % 2MB = x & ((2^21)-1)
            return address & ((1 << 21) - 1);
        }
    }
}
