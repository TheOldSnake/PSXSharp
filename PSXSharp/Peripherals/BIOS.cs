using PSXSharp.Core;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace PSXSharp {
    public unsafe class BIOS {
        public const uint SIZE = 512 * 1024;
        public const uint BASE_ADDRESS = 0x1FC00000;
        public Range Range = new Range(BASE_ADDRESS, SIZE);

        private readonly byte* Data = (byte*)NativeMemoryManager.AllocateNativeMemory(SIZE);
        public byte* NativeAddress => Data;
        public BIOS(string? path) {
            if (string.IsNullOrEmpty(path)) {
                throw new Exception("BIOS Path is null or empty");
            }

            byte[] loadedFile = File.ReadAllBytes(path);

            if (loadedFile.Length != SIZE) {
                throw new Exception("BIOS file is not valid");
            }

            fixed (byte* srcPtr = loadedFile) {
                NativeMemoryManager.CopyNativeMemory(srcPtr, Data, SIZE);
            }
        }
        
        public uint ReadWord(UInt32 address) {
            uint offset = address - Range.Start;
            uint b0 = Data[offset + 0];
            uint b1 = Data[offset + 1];
            uint b2 = Data[offset + 2];
            uint b3 = Data[offset + 3];

            return (b0 | (b1 << 8) | (b2 << 16) | (b3 << 24));
        }

        public byte ReadByte(uint address) {
            uint offset = address - Range.Start;
            return Data[offset];
        }

        public ushort ReadHalf(uint address) {
            uint offset = address - Range.Start;
            uint b0 = Data[offset + 0];
            uint b1 = Data[offset + 1];
            return (ushort)(b0 | (b1 << 8));
        }
    }
}
