using System;

namespace PSXSharp {
    internal class IRQ_CONTROL {
        public static Range range = new Range(0x1f801070, 8);

        public static UInt32 I_STAT = 0;  //IRQ Status 
        public static UInt32 I_MASK = 0;  //IRQ Mask 

        public static uint Read(uint address) {
            uint offset = address - range.start;
            switch (offset) {
                case 0: return I_STAT;       //& mask?
                case 4: return I_MASK;
                default: throw new Exception("unhandled IRQ read at offset " + offset);
            }
        }

        public static void Write(uint address, ushort value) {
            uint offset = address - range.start;
            switch (offset) {
                case 0: I_STAT = I_STAT & value; break;
                case 4: I_MASK = value; break;
                default: throw new Exception("unhandled IRQ write at offset " + offset);
            }
            //Console.WriteLine("IRQ EN: " + (I_STAT & I_MASK));
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
