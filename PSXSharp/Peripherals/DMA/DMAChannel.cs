using System;
using System.Net;

namespace PSXSharp {
    public class DMAChannel {
        private readonly uint PortNumber;   

        //Channel control register
        private uint Enabled;
        private uint Direction;
        private uint Step;
        private uint Sync;
        private uint Trigger;
        private uint Chop;
        private uint ChopDMAWindowSize;
        private uint ChopCPUWindowSize;
        private uint ReadWrite;     

        //Base address register
        private uint BaseAddress;

        //Block Control register
        private uint BlockSize;              //[0:15]
        private uint BlockCount;             //[16:31]

        public enum DirectionType { ToRam = 0, FromRam = 1 }
        public enum StepAmount { Increment = 0, Decrement = 1 }
        public enum SyncType { Manual = 0, Request = 1, LinkedList = 2 }
        public uint ChannelPort => PortNumber;
        public DirectionType TransferDirection => (DirectionType)Direction;
        public StepAmount TransferStep => (StepAmount)Step;
        public SyncType TransferSync => (SyncType)Sync;
        public bool IsActive => Sync == 0 ? (Trigger & Enabled) == 1 : Enabled == 1;
        public uint GetBaseAddress() => BaseAddress;
        private void SetBaseAddress(uint value) => BaseAddress = value & 0xFFFFFF;   //Only bits [0:23]

        public DMAChannel(uint port) {
            PortNumber = port;
            Enabled = 0;
            Direction = (uint)DirectionType.ToRam;
            Step = (uint)StepAmount.Increment;
            Sync = (uint)SyncType.Manual;
            Chop = 0;
            ChopDMAWindowSize = 0;
            ChopCPUWindowSize = 0;
            ReadWrite = 0;
        }

        public uint ReadRegister(uint reg) {
            switch (reg) {
                case 0x0: return GetBaseAddress();
                case 0x4: return ReadBlockControl();
                case 0x8: return ReadChannelControl();
                default: throw new Exception($"Unhandled DMA Register read at: {reg:X}");
            }
        }

        public void WriteRegister(uint reg, uint value) {
            switch (reg) {
                case 0x0: SetBaseAddress(value); break;
                case 0x4: WriteBlockControl(value); break;
                case 0x8: WriteChannelControl(value); break;
                default: throw new Exception($"Unhandled DMA Register write at: {reg:X8} value = {value:X}");
            }
        }

        private void WriteBlockControl(uint value) {
            //BC/BS/BA can be in range 0001h..FFFFh (or 0=10000h)
            BlockSize = value & 0xFFFF;
            BlockCount = value >> 16;
            
            if(BlockSize == 0) {
                BlockCount = 0x10000;
            }

            if (BlockSize == 0) {
                BlockSize = 0x10000;
            }
        }

        private uint ReadBlockControl() {
            uint bc = BlockCount;
            uint bs = BlockSize;
            return (bc << 16) | bs;
        }

        private uint ReadChannelControl() {
            uint control = 0;
            control |= Direction;
            control |= Step << 1;
            control |= Chop << 8;
            control |= Sync << 9;
            control |= ChopDMAWindowSize << 16;
            control |= ChopCPUWindowSize << 20;
            control |= Enabled << 24;
            control |= Trigger << 28;
            control |= ReadWrite << 29;
            return control;
        }

        private void WriteChannelControl(uint value) {
            Direction = value & 1;
            Step = (value >> 1) & 1;
            Chop = (value >> 8) & 1;
            Sync = (value >> 9) & 3;
            ChopDMAWindowSize = (byte)((value >> 16) & 7);
            ChopCPUWindowSize = (byte)((value >> 20) & 7);
            Enabled = (value >> 24) & 1;
            Trigger = (value >> 28) & 1;
            ReadWrite = (byte)((value >> 29) & 3);
            if (Sync == 3) {
                throw new Exception("Reserved DMA sync mode: 3");
            }
        }

        public uint GetTransferSize() {
            SyncType currentSync = (SyncType)Sync;
            switch (currentSync) {
                case SyncType.Manual: return BlockSize;
                case SyncType.Request: return BlockCount * BlockSize;
                case SyncType.LinkedList: return 0;  //Linkedlist does not use this size.
                default: throw new Exception($"Unknown Sync value = {Sync}");
            }
        }

        public void Done() {
            Enabled = 0;
            Trigger = 0;
        }
    }
}
