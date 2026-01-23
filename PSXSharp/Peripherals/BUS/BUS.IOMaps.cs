using System;

namespace PSXSharp {
    public partial class BUS {
        public struct IO32 { 
            public Range Range;
            public Func<uint, uint>? Read; 
            public Action<uint, uint>? Write;
        }

        public struct IO16 { 
            public Range Range;
            public Func<uint, ushort>? Read; 
            public Action<uint, ushort>? Write;
        }

        public struct IO8 {  
            public Range Range;
            public Func<uint, byte>? Read; 
            public Action<uint, byte>? Write;
        }

        private IO32[] CreateIO32Map() {
            IO32[] map = [
                new IO32 { Range = IRQ_CONTROL.Range,       Read = IRQ_CONTROL.ReadWord,       Write = IRQ_CONTROL.WriteWord },
                new IO32 { Range = DMA.Range,               Read = DMA.ReadWord,               Write = DMA.WriteWord },
                new IO32 { Range = GPU.Range,               Read = GPU.LoadWord,               Write = GPU.WriteWord },
                new IO32 { Range = SPU.Range,               Read = SPU.ReadWord,               Write = null },
                new IO32 { Range = Timer0.Range,            Read = Timer0.ReadWord,            Write = Timer0.WriteWord },
                new IO32 { Range = Timer1.Range,            Read = Timer1.ReadWord,            Write = Timer1.WriteWord },
                new IO32 { Range = Timer2.Range,            Read = Timer2.ReadWord,            Write = Timer2.WriteWord },
                new IO32 { Range = JOY_IO.Range,            Read = JOY_IO.ReadWord,            Write = null },
                new IO32 { Range = SerialIO1.Range,         Read = SerialIO1.ReadWord,         Write = null },
                new IO32 { Range = MemoryControl.Range,     Read = MemoryControl.ReadWord,     Write = MemoryControl.WriteWord },
                new IO32 { Range = MDEC.Range,              Read = MDEC.ReadWord,              Write = MDEC.WriteWord },
                new IO32 { Range = RamSize.Range,           Read = RamSize.ReadWord,           Write = RamSize.WriteWord },
                new IO32 { Range = CacheControl.Range,      Read = CacheControl.ReadWord,      Write = CacheControl.WriteWord },
            ];

            Array.Sort(map, (a, b) => a.Range.Start.CompareTo(b.Range.Start));
            return map;
        }

        private IO16[] CreateIO16Map() {
            IO16[] map = [
                new IO16 { Range = SPU.Range,            Read = SPU.ReadHalf,            Write = SPU.WriteHalf },
                new IO16 { Range = IRQ_CONTROL.Range,    Read = IRQ_CONTROL.ReadHalf,    Write = IRQ_CONTROL.WriteHalf },
                new IO16 { Range = DMA.Range,            Read = DMA.ReadHalf,            Write = DMA.WriteHalf },
                new IO16 { Range = Timer0.Range,         Read = Timer0.ReadHalf,         Write = Timer0.WriteHalf },
                new IO16 { Range = Timer1.Range,         Read = Timer1.ReadHalf,         Write = Timer1.WriteHalf },
                new IO16 { Range = Timer2.Range,         Read = Timer2.ReadHalf,         Write = Timer2.WriteHalf },
                new IO16 { Range = JOY_IO.Range,         Read = JOY_IO.ReadHalf,         Write = JOY_IO.WriteHalf },
                new IO16 { Range = SerialIO1.Range,      Read = SerialIO1.ReadHalf,      Write = SerialIO1.WriteHalf },
                new IO16 { Range = MemoryControl.Range,  Read = MemoryControl.ReadHalf,  Write = MemoryControl.WriteHalf },
            ];

            Array.Sort(map, (a, b) => a.Range.Start.CompareTo(b.Range.Start));
            return map;
        }

        private IO8[] CreateIO8Map() {
            IO8[] map = [
                new IO8 { Range = CDROM.Range,       Read = CDROM.ReadByte,       Write = CDROM.WriteByte },
                new IO8 { Range = DMA.Range,         Read = DMA.ReadByte,         Write = DMA.WriteByte },
                new IO8 { Range = JOY_IO.Range,      Read = JOY_IO.ReadByte,      Write = JOY_IO.WriteByte },
                new IO8 { Range = SerialIO1.Range,   Read = SerialIO1.ReadByte,   Write = SerialIO1.WriteByte },
                new IO8 { Range = Expansion1.Range,  Read = Expansion1.ReadByte,  Write = Expansion1.WriteByte },
                new IO8 { Range = Expansion2.Range,  Read = Expansion2.ReadByte,  Write = Expansion2.WriteByte },
            ];

            Array.Sort(map, (a, b) => a.Range.Start.CompareTo(b.Range.Start));
            return map;
        }
    }
}
