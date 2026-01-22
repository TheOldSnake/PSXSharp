using System;

namespace PSXSharp {
    public class RAM_SIZE {      //Configured by bios
        public Range Range = new Range(0x1f801060, 4);
        uint RamSize = 0x00000B88; //(usually 00000B88h) (or 00000888h)
        public int RamReadDelay => (int)((RamSize >> 7) & 1);

        public void WriteWord(uint address, uint value) {
            if (address != 0x1f801060) {
                throw new Exception();
            }

            RamSize = value;
        }

        public uint ReadWord(uint address) {
            return RamSize;
        }
    }
}
