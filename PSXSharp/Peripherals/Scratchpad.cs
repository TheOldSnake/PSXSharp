using PSXSharp.Core;
using System;

namespace PSXSharp {
    public unsafe class Scratchpad {
        public const uint Size = 0x400;
        public Range Range = new Range(0x1F800000, Size);
        private byte* Data = (byte*)NativeMemoryManager.AllocateNativeMemory(Size);
        public byte* NativeAddress => Data;

        public UInt32 ReadWord(UInt32 address) {
            uint offset = address - Range.start;

            UInt32 b0 = Data[offset + 0];
            UInt32 b1 = Data[offset + 1];
            UInt32 b2 = Data[offset + 2];
            UInt32 b3 = Data[offset + 3];

            return b0 | (b1 << 8) | (b2 << 16) | (b3 << 24);
        }

        public void WriteWord(UInt32 address, UInt32 value) {
            uint offset = address - Range.start;

            byte b0 = (byte)value;
            byte b1 = (byte)(value >> 8);
            byte b2 = (byte)(value >> 16);
            byte b3 = (byte)(value >> 24);

            Data[offset + 0] = b0;
            Data[offset + 1] = b1;
            Data[offset + 2] = b2;
            Data[offset + 3] = b3;
        }

        internal UInt16 ReadHalf(UInt32 address) {
            uint offset = address - Range.start;

            UInt16 b0 = Data[offset + 0];
            UInt16 b1 = Data[offset + 1];

            return (UInt16)(b0 | (b1 << 8));
        }
        internal void WriteHalf(UInt32 address, UInt16 value) {
            uint offset = address - Range.start;

            byte b0 = (byte)value;
            byte b1 = (byte)(value >> 8);

            Data[offset + 0] = b0;
            Data[offset + 1] = b1;
        }

        internal byte ReadByte(UInt32 address) {
            uint offset = address - Range.start;
            return Data[offset];
        }

        internal void WriteByte(UInt32 address, byte value) {
            uint offset = address - Range.start;
            Data[offset] = value;
        }
    }
}
