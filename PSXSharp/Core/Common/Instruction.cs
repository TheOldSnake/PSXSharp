using System;

namespace PSXSharp.Core.Common {
    public struct Instruction {
        public uint Value;                                              //Bits  [31:0]
        public uint Op => Value >> 26;                                  //Bits  [31:26]
        public uint Rt => Value >> 16 & 0x1F;                           //Bits  [20:16]
        public uint Imm => Value & 0xFFFF;                              //Bits  [15:0]
        public uint SignedImm => (uint)(short)(Value & 0xFFFF);         //Bits  [15:0] but sign extended to 32-bits
        public uint Rs => Value >> 21 & 0x1F;                           //Bits  [25:21]
        public uint Rd => Value >> 11 & 0x1F;                           //Bits  [15:11]
        public uint Sa => Value >> 6 & 0x1F;                            //Bits  [10:6]
        public uint Sub => Value & 0x3f;                                //Bits  [5:0]
        public uint JumpImm => Value & 0x3ffffff;                       //Bits  [26:0]
    }
}
