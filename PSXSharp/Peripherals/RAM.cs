using PSXSharp.Core;
using PSXSharp.Core.x64_Recompiler;

namespace PSXSharp {
    public unsafe class RAM {
        //2MB RAM can be mirrored to the first 8MB (strangely, enabled by default)
        public Range Range = new Range(0x00000000, 8*1024*1024);
        byte* Data = NativeMemoryManager.AllocateGuestMemory();

        public uint LoadWord(uint address) {
            uint offset = address - Range.start;
            uint final = Mirror(offset);

            byte b0 = Data[final + 0];
            byte b1 = Data[final + 1];
            byte b2 = Data[final + 2];
            byte b3 = Data[final + 3];

            return (uint)(b0 | (b1 << 8) | (b2 << 16) | (b3 << 24));
        }

        public void StoreWord(uint address, uint value) {
            uint offset = address - Range.start;
            uint final = Mirror(offset);

            byte b0 = (byte)value;
            byte b1 = (byte)(value >> 8);
            byte b2 = (byte)(value >> 16);
            byte b3 = (byte)(value >> 24);

            Data[final + 0] = b0;
            Data[final + 1] = b1;
            Data[final + 2] = b2;
            Data[final + 3] = b3;
            CPUWrapper.GetCPUInstance().SetInvalidRAMBlock(final >> 2);
        }

        public ushort LoadHalf(uint address) {
            uint offset = address - Range.start;
            uint final = Mirror(offset);

            ushort b0 = Data[final + 0];
            ushort b1 = Data[final + 1];

            return ((ushort)(b0 | (b1 << 8)));
        }

        public void StoreHalf(uint address, ushort value) {
            uint offset = address - Range.start;
            uint final = Mirror(offset);

            byte b0 = (byte)value;
            byte b1 = (byte)(value >> 8);

            Data[final + 0] = b0;
            Data[final + 1] = b1;
            CPUWrapper.GetCPUInstance().SetInvalidRAMBlock(final >> 2);
        }

        public byte LoadByte(uint address) {
            uint offset = address - Range.start;
            uint final = Mirror(offset);

            return Data[final];
        }

        public void StoreByte(uint address, byte value) {
            uint offset = address - Range.start;
            uint final = Mirror(offset);
            Data[final] = value;
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
