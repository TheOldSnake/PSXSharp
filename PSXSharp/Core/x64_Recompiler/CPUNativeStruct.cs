using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static PSXSharp.Core.CPU;

namespace PSXSharp.Core.x64_Recompiler {

        [StructLayout(LayoutKind.Sequential)]
        public struct CPUNativeStruct {
            public InlineArray32<uint> GPR;             //Offset = [000]
            public uint PC;                             //Offset = [128]
            public uint Next_PC;                        //Offset = [132]
            public uint Current_PC;                     //Offset = [136]
            public uint HI;                             //Offset = [140]
            public uint LO;                             //Offset = [144]
            public uint Padding;                        //Offset = [148] --> 4 bytes Padding 
            public RegisterLoad ReadyLoad;              //Offset = [152] --> Size = 4*2 = 8 bytes
            public RegisterLoad DelayedLoad;            //Offset = [160] --> Size = 4*2 = 8 bytes
            public RegisterLoad DirectLoad;             //Offset = [168] --> Size = 4*2 = 8 bytes         
            public uint Branch;                         //Offset = [176]
            public uint DelaySlot;                      //Offset = [180]
            public uint COP0_SR;                        //Offset = [184]
            public uint COP0_Cause;                     //Offset = [188]
            public uint COP0_EPC;                       //Offset = [192]
            public ulong CurrentCycle;                  //Offset = [200] --> Aligned 
        }

        [InlineArray(32)]
        public struct InlineArray32<T> {
            private T _e0;
        }
    }
