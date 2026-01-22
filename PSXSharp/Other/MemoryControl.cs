using System;

namespace PSXSharp {

    public class MemoryControl {
        public Range Range = new Range(0x1F801000, 0x24);

        //Addresses
        const uint EXPANSION1_BASE = 0x1F801000;
        const uint EXPANSION2_BASE = 0x1F801004;
        const uint EXPANSION1_DELAY = 0x1F801008;
        const uint EXPANSION3_DELAY = 0x1F80100C;
        const uint BIOS_ROM = 0x1F801010;
        const uint SPU_DELAY = 0x1F801014;
        const uint CDROM_DELAY = 0x1F801018;
        const uint EXPANSION2_DELAY = 0x1F80101C;
        const uint COMMON_DELAY = 0x1F801020;

        //Values
         uint EXPANSION1_BASE_VALUE = 0x1F000000;
         uint EXPANSION2_BASE_VALUE = 0x1F802000;
         uint EXPANSION1_DELAY_VALUE = 0x0013243F;
         uint EXPANSION3_DELAY_VALUE = 0x00003022;
         uint BIOS_ROM_VALUE = 0x0013243F;
         uint SPU_DELAY_VALUE = 0x200931E1;
         uint CDROM_DELAY_VALUE = 0x00020843;
         uint EXPANSION2_DELAY_VALUE = 0x00070777;
         uint COMMON_DELAY_VALUE = 0x00031125;

        //Writing 16-bit will write a full 32-bit, according to "Unpredictable Things" in PSX-SPX
        public void WriteHalf(uint address, ushort value) => WriteWord(address, value);
        public ushort ReadHalf(uint address) {
            uint word = ReadWord(address);
            if ((address & 0x2) == 1) {
                word >>= 16;
            }

            return (ushort)word;
        }

        public uint ReadWord(uint address) {
            switch (address) {
                case EXPANSION1_BASE: return EXPANSION1_BASE_VALUE;
                case EXPANSION2_BASE: return EXPANSION2_BASE_VALUE;
                case EXPANSION1_DELAY: return EXPANSION1_DELAY_VALUE;
                case EXPANSION3_DELAY: return EXPANSION3_DELAY_VALUE;
                case BIOS_ROM: return BIOS_ROM_VALUE;
                case SPU_DELAY: return SPU_DELAY_VALUE;
                case CDROM_DELAY: return CDROM_DELAY_VALUE;
                case EXPANSION2_DELAY: return EXPANSION2_DELAY_VALUE;
                case COMMON_DELAY: return COMMON_DELAY_VALUE;
                default: throw new Exception("Memory Control Read at address: " + address.ToString("x"));
            }
         }

        public void WriteWord(uint address, uint value) {
            switch (address) {
                case EXPANSION1_BASE: EXPANSION1_BASE_VALUE = value; break; 
                case EXPANSION2_BASE: EXPANSION2_BASE_VALUE = value; break;
                case EXPANSION1_DELAY: EXPANSION1_DELAY_VALUE = value; break;
                case EXPANSION3_DELAY: EXPANSION3_DELAY_VALUE = value; break;
                case BIOS_ROM: BIOS_ROM_VALUE = value; break;
                case SPU_DELAY: SPU_DELAY_VALUE = value; break;
                case CDROM_DELAY: CDROM_DELAY_VALUE = value; break;
                case EXPANSION2_DELAY: EXPANSION2_DELAY_VALUE = value; break;
                case COMMON_DELAY: COMMON_DELAY_VALUE = value; break;
                default: throw new Exception("Memory Control Write at address: " + address.ToString("x"));
            }
        }
    }
}
