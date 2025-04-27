using PSXSharp.Core;
using PSXSharp.Core.x64_Recompiler;
using System;

namespace PSXSharp {
    public class CACHECONTROL {

        public Range Range = new Range(0xfffe0130, 4);      
        public void WriteWord(uint address, uint value) {
            //Invalidate all ram blocks when this register is written
            CPUWrapper.GetCPUInstance().SetInvalidAllRAMBlocks();
        }
    }
}
