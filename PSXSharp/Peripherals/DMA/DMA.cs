using System;
using System.Net;

namespace PSXSharp {
    public class DMA {
        public const uint StartAddress = 0x1F801080;
        public Range Range = new Range(StartAddress, 0x80);

        //Channel numbers constants 
        public const uint MDECIN_CHANNEL    = 0;
        public const uint MDECOUT_CHANNEL   = 1;
        public const uint GPU_CHANNEL       = 2;
        public const uint CDROM_CHANNEL     = 3;
        public const uint SPU_CHANNEL       = 4;
        public const uint PIO_CHANNEL       = 5;
        public const uint OTC_CHANNEL       = 6;

        public bool HasIRQ => _DICR.MasterFlag == 1;
        public bool IsIRQEnabled(uint channel) => _DICR.IsIRQEnabled(channel);
        public void SetIRQ(uint channel) => _DICR.SetIRQ(channel);
        public void SetHandler(Action<DMAChannel> dmaHandler) => BUS_DMA_Handler = dmaHandler;  

        private Action<DMAChannel> BUS_DMA_Handler = null!;

        private readonly DMAChannel[] Channels = [
            new DMAChannel(MDECIN_CHANNEL),
            new DMAChannel(MDECOUT_CHANNEL),
            new DMAChannel(GPU_CHANNEL),
            new DMAChannel(CDROM_CHANNEL),
            new DMAChannel(SPU_CHANNEL),
            new DMAChannel(PIO_CHANNEL),
            new DMAChannel(OTC_CHANNEL)
        ];

        //DMA Control Register - DPCR [0x1F8010F0]
        public uint DPCR = 0x07654321;

        //DMA Interrupt Control Register - DICR [0x1F8010F4]
        private DICR _DICR;
        public struct DICR { 
            public uint CompletionInterruptControl;           //Bits [0:6]
                                                              //Bits [7:14]  Unused
            public uint BUSError;                             //Bits [15] (higher priority than Bit 23)
            public uint IRQMask;                              //Bits [16:22] IRQ masking for indivisual channels 
            public uint MasterEnabled;                        //Bits [23]
            public uint IRQFlags;                             //Bits [24:30] indivisual channels (reset by setting 1)
            public readonly uint MasterFlag => ((BUSError == 1) || (MasterEnabled == 1 && (IRQMask & IRQFlags) > 0)) ? 1U : 0U;  //Bit [31] (Read Only)

            public void SetIRQ(uint channel) => IRQFlags |= 1U << (int)channel;
            public bool IsIRQEnabled(uint channel) => ((IRQMask >> (int)channel) & 1) == 1;
            public readonly uint Read32 => CompletionInterruptControl | (BUSError << 15) | (IRQMask << 16) | (MasterEnabled << 23) | (IRQFlags << 24) | (MasterFlag << 31);
            public void Write32(uint value) {
                CompletionInterruptControl = (byte)(value & 0x7F);
                BUSError = (value >> 15) & 1;
                IRQMask = (value >> 16) & 0x7F;
                MasterEnabled = (value >> 23) & 1;
                IRQFlags &= ~((value >> 24) & 0x7F);
            }

            public ushort Read16(uint address) {
                switch (address) {
                    case 0x1F8010F4: return (byte)(Read32 & 0xFFFF);
                    case 0x1F8010F6: return (byte)((Read32 >> 16) & 0xFFFF);
                    default: throw new Exception($"Unhandeled DMA ReadByte at {address:X8}");
                }
            }

            public void Write16(uint address, ushort value) {
                /*TODO*/
            }

            public byte Read8(uint address) {
                switch (address) {
                    case 0x1F8010F4: return (byte)(Read32 & 0xFF);
                    case 0x1F8010F5: return (byte)((Read32 >> 8) & 0xFF);
                    case 0x1F8010F6: return (byte)((Read32 >> 16) & 0xFF);
                    case 0x1F8010F7: return (byte)((Read32 >> 24) & 0xFF);
                    default: throw new Exception($"Unhandeled DMA ReadByte at {address:X8}");
                }
            }

            public void Write8(uint address, byte value) {
                //Write to parts of the register depending on which byte is accessed
                switch (address) {
                    case 0x1F8010F4: //0 - 7
                        CompletionInterruptControl = (uint)(value & 0x7F);
                        break;

                    case 0x1F8010F5: //8 - 15
                        BUSError = (uint)(value >> 7);
                        break;

                    case 0x1F8010F6: //16 - 23
                        IRQMask = (uint)(value & 0x7F);
                        MasterEnabled = (uint)(value >> 7);
                        break;

                    case 0x1F8010F7: //24 - 31
                        IRQFlags &= (uint) ~(value & 0x7F);
                        //Bit 31 is read only, we don't write it.
                        break;
                }
            }
        }

        public uint ReadWord(uint address) {
            //DPCR register
            if (address == 0x1F8010F0) {
                return DPCR;
            }

            //DICR register
            if (address == 0x1F8010F4) {
                return _DICR.Read32;
            }

            //Channel Specific
            uint register = address & 0xF;                     //Bits [0:3] specify the register number
            uint channelNumber = (address & 0x70) >> 4;        //Bits [5:7] specify the channel number (or general registers when == 7)

            if (channelNumber <= 6) {
                //Channel register
                DMAChannel channel = Channels[channelNumber];
                return channel.ReadRegister(register);
            } else {
                throw new Exception($"Unhandled DMA read at address: {address:X8}");
            }
        }

        public void WriteWord(uint address, uint value) {
            //DPCR register
            if (address == 0x1F8010F0) {
                DPCR = value;
                return;
            }

            //DICR register
            if (address == 0x1F8010F4) {
                _DICR.Write32(value);
                return;
            }

            //Channel Specific
            uint register = address & 0xF;                     //Bits [0:3] specify the register number
            uint channelNumber = (address & 0x70) >> 4;        //Bits [5:7] specify the channel number (or general registers when == 7)

            if (channelNumber <= 6) {
                //Channel register
                DMAChannel channel = Channels[channelNumber];
                channel.WriteRegister(register, value);

                //Check if the channel got activated, and handle the transfer
                if (channel.IsActive) {
                    BUS_DMA_Handler(channel);
                }

            } else {
                throw new Exception($"Unhandled DMA write at: {address:X8} value = {value:X}");
            }      
        }

        public ushort ReadHalf(uint address) {  
            return (ushort)ReadWord(address);
        }

        public void WriteHalf(uint address, ushort value) { 
            WriteWord(address, value);  
        }

        public byte ReadByte(uint address) {
            //Seems that only DICR is accessed via read/write byte
            if (address >= 0x1F8010F4 && address <= 0x1F8010F7) {
               return _DICR.Read8(address);
            } else {
                throw new Exception($"Unhandeled DMA ReadByte at {address:X8}");
            }
        }

        public void WriteByte(uint address, byte value) {
            //Seems that only DICR is accessed via read/write byte
            if (address >= 0x1F8010F4 && address <= 0x1F8010F7) {
                _DICR.Write8(address, value);
            } else {
                throw new Exception($"Unhandeled DMA WriteByte at {address:X8} value = {value:X}");
            }
        }
    }
}