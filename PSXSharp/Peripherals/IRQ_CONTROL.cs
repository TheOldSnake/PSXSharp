using System;

namespace PSXSharp {
    internal class IRQ_CONTROL {
        public static Range Range = new Range(0x1f801070, 8);

        public static UInt32 I_STAT = 0;  //IRQ Status 
        public static UInt32 I_MASK = 0;  //IRQ Mask 

        public static uint ReadWord(uint address) {
            uint offset = address - Range.Start;
            switch (offset) {
                case 0: return I_STAT;       //& mask?
                case 4: return I_MASK;
                default: throw new Exception("unhandled IRQ read at offset " + offset);
            }
        }

        public static void WriteWord(uint address, uint value) {
            uint offset = address - Range.Start;
            switch (offset) {
                case 0: I_STAT = I_STAT & value; break;
                case 4: I_MASK = value; break;
                default: throw new Exception("unhandled IRQ write at offset " + offset);
            }
            //Console.WriteLine("IRQ EN: " + (I_STAT & I_MASK));
        }

        public static ushort ReadHalf(uint address) {
            //The upper 16-bits are garbage, no need to check and shift
            uint word = ReadWord(address);
            return (ushort)word;
        }

        public static void WriteHalf(uint address, ushort value) {
            //The upper 16-bits are garbage, only write to the lower part 
            if ((address & 0x2) == 0) {
                WriteWord(address, value);
            }
        }

        public static void IRQsignal(int bitNumber) {
            I_STAT = I_STAT | (ushort)(1 << bitNumber);
        }

        public static bool isRequestingIRQ() {
            return (I_STAT & I_MASK) > 0;  
        }

        public static int readIRQbit(int bitNumber) {
            return (int)(I_STAT >> bitNumber) & 1; 
        }
    }
}
