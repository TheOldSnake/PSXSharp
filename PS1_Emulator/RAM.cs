﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PS1_Emulator {
    internal class RAM {

        public Range range = new Range(0x00000000, 2*1024*1024);
        byte[] data;
        public RAM() {

            this.data = new byte[2*1024*1024];
            
        
        }
        public UInt32 read(UInt32 offset) {

            CPU.incrementSynchronizer();

            UInt32 b0 = data[offset + 0];
            UInt32 b1 = data[offset + 1];
            UInt32 b2 = data[offset + 2];
            UInt32 b3 = data[offset + 3];

            return (b0 | (b1 << 8) | (b2 << 16) | (b3 << 24));
        }
        public void write(UInt32 offset, UInt32 value) {
            CPU.incrementSynchronizer();


            byte b0 = (byte) value;
            byte b1 = (byte)(value >> 8);
            byte b2 = (byte)(value >> 16);
            byte b3 = (byte)(value >> 24);


            data[offset + 0] = b0;
            data[offset + 1] = b1;
            data[offset + 2] = b2;
            data[offset + 3] = b3;

        }

        internal void store8(UInt32 offset, byte value) {
            CPU.incrementSynchronizer();

            data[offset] = value;
        }
        internal void store16(UInt32 offset, UInt16 value) {
            CPU.incrementSynchronizer();

            byte b0 = (byte)value;
            byte b1 = (byte)(value >> 8);

            data[offset + 0] = b0;
            data[offset + 1] = b1;
        }
        internal byte load8(UInt32 offset) {

            CPU.incrementSynchronizer();


            return data[offset];
        }

        internal UInt16 load16(UInt32 offset) {

            CPU.incrementSynchronizer();

            UInt16 b0 = data[offset + 0];
            UInt16 b1 = data[offset + 1];

            return ((UInt16)(b0 | (b1 << 8)));
        }

    }
}
