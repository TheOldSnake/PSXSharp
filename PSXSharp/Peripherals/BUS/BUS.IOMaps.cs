using System;

namespace PSXSharp {
    public partial class BUS {
        private (Range Range, Func<uint, uint>? Read, Action<uint, uint>? Write)[] CreateIO32Map() {
            return [
                (IRQ_CONTROL.Range,   IRQ_CONTROL.ReadWord,   IRQ_CONTROL.WriteWord),
                (DMA.Range,           DMA.ReadWord,           DMA.WriteWord),
                (GPU.Range,           GPU.LoadWord,           GPU.WriteWord),
                (SPU.Range,           SPU.ReadWord,           null),
                (Timer0.Range,        Timer0.ReadWord,        Timer0.WriteWord),
                (Timer1.Range,        Timer1.ReadWord,        Timer1.WriteWord),
                (Timer2.Range,        Timer2.ReadWord,        Timer2.WriteWord),
                (JOY_IO.Range,        JOY_IO.ReadWord,        null),
                (SerialIO1.Range,     SerialIO1.ReadWord,     null),
                (MemoryControl.Range, MemoryControl.ReadWord, MemoryControl.WriteWord),
                (MDEC.Range,          MDEC.ReadWord,          MDEC.WriteWord),
                (RamSize.Range,       RamSize.ReadWord,       RamSize.WriteWord),
                (CacheControl.Range,  CacheControl.ReadWord,  CacheControl.WriteWord),
            ];
        }

        private (Range Range, Func<uint, ushort>? Read, Action<uint, ushort>? Write)[] CreateIO16Map() {
            return [
                (SPU.Range,           SPU.ReadHalf,           SPU.WriteHalf),
                (IRQ_CONTROL.Range,   IRQ_CONTROL.ReadHalf,   IRQ_CONTROL.WriteHalf),
                (DMA.Range,           DMA.ReadHalf,           DMA.WriteHalf),
                (Timer0.Range,        Timer0.ReadHalf,        Timer0.WriteHalf),
                (Timer1.Range,        Timer1.ReadHalf,        Timer1.WriteHalf),
                (Timer2.Range,        Timer2.ReadHalf,        Timer2.WriteHalf),
                (JOY_IO.Range,        JOY_IO.ReadHalf,        JOY_IO.WriteHalf),
                (SerialIO1.Range,     SerialIO1.ReadHalf,     SerialIO1.WriteHalf),
                (MemoryControl.Range, MemoryControl.ReadHalf, MemoryControl.WriteHalf),
            ];
        }

        private (Range Range, Func<uint, byte>? Read, Action<uint, byte>? Write)[] CreateIO8Map() {
            return [
                (CDROM.Range,      CDROM.ReadByte,      CDROM.WriteByte),
                (DMA.Range,        DMA.ReadByte,        DMA.WriteByte),
                (JOY_IO.Range,     JOY_IO.ReadByte,     JOY_IO.WriteByte),
                (SerialIO1.Range,  SerialIO1.ReadByte,  SerialIO1.WriteByte),
                (Expansion1.Range, Expansion1.ReadByte, Expansion1.WriteByte),
                (Expansion2.Range, Expansion2.ReadByte, Expansion2.WriteByte),
            ];
        }
    }
}
