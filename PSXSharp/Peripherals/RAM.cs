using PSXSharp.Core;
using PSXSharp.Core.x64_Recompiler;
using System.Runtime.CompilerServices;

namespace PSXSharp {
    public unsafe class RAM {
        //2MB RAM can be mirrored to the first 8MB (strangely, enabled by default)
        public Range Range = new Range(0x00000000, 8*1024*1024);
        byte* Data = NativeMemoryManager.AllocateGuestMemory();

        public uint LoadWord(uint address) {
            uint offset = address - Range.start;
            uint final = Mirror(offset);
            return Unsafe.Read<uint>(Data + final);
        }

        public void StoreWord(uint address, uint value) {
            uint offset = address - Range.start;
            uint final = Mirror(offset);
            Unsafe.Write<uint>(Data + final, value);
            CPUWrapper.GetCPUInstance().SetInvalidRAMBlock(final >> 2);
        }

        public ushort LoadHalf(uint address) {
            uint offset = address - Range.start;
            uint final = Mirror(offset);
            return Unsafe.Read<ushort>(Data + final);
        }

        public void StoreHalf(uint address, ushort value) {
            uint offset = address - Range.start;
            uint final = Mirror(offset);
            Unsafe.Write<ushort>(Data + final, value);
            CPUWrapper.GetCPUInstance().SetInvalidRAMBlock(final >> 2);
        }

        public byte LoadByte(uint address) {
            uint offset = address - Range.start;
            uint final = Mirror(offset);
            return Unsafe.Read<byte>(Data + final);
        }

        public void StoreByte(uint address, byte value) {
            uint offset = address - Range.start;
            uint final = Mirror(offset);
            Unsafe.Write<byte>(Data + final, value);
            CPUWrapper.GetCPUInstance().SetInvalidRAMBlock(final >> 2);
        }

        public uint Mirror(uint address) {
            //Handle memory mirror, but without %  
            //x % (2^n) is equal to x & ((2^n)-1)
            //So x % 2MB = x & ((2^21)-1)
            return address & ((1 << 21) - 1);
        }

        public byte* GetMemoryPointer() {
            return Data;
        }

        public ulong GetMemoryPointer2() {
            return (ulong)&Data[0];
        }
    }
}
