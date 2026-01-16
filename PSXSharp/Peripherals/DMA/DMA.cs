using System;
using System.Net.NetworkInformation;

namespace PSXSharp {
    public class DMA {
        public Range range = new Range(0x1f801080, 0x80);
        public UInt32 control = 0x07654321;

        //TODO: Refactor!

        //Interrupt register
        public UInt32 master_enabled;     //Bit 23
        public byte ch_irq_en;            //irq enable for indivisual channels Bits [22:16]
        public byte ch_irq_flags;         //Bits [30:24] indivisual channels (reset by setting 1)
        public UInt32 force_irq;          //Bit 15 (higher priority than Bit 23)
        public byte read_write;           //Bits [5:0]
        public DMAChannel[] channels;

        public DMAChannel reject = null;

        Action<DMAChannel> HandleDMABus;
        Action<DMAChannel> HandleDMALinkedListBus;

        public DMA(Action<DMAChannel> dmaHandler, Action<DMAChannel> dmaLinkedListHandler) {
         channels = new DMAChannel[7];
            for (int i = 0; i<channels.Length; i++) {
                channels[i] = new DMAChannel();
                channels[i].set_portnum((UInt32)i);
            }   

            HandleDMABus = dmaHandler;
            HandleDMALinkedListBus = dmaLinkedListHandler;
        }

        public UInt32 ReadWord(UInt32 address) {
            uint offset = address - range.start;

            UInt32 ch = (offset & 0x70) >> 4;           //Bits [7:5] for the channel number
            UInt32 field = (offset & 0xf);              //Bits [3:0] for the field number

            switch (ch) {               //Reading a field for a specific channel
                case 0:
                case 1:
                case 2:
                case 3:
                case 4:
                case 5:
                case 6:

                    switch (field) {
                        case 0: return channels[ch].read_base_addr();                           
                        case 4: return channels[ch].read_block_control();
                        case 8: return channels[ch].read_control();
                        default: throw new Exception("Unhandled DMA read at offset: " + offset.ToString("X"));
                    }

                case 7:         //case 7 is general fields 
                    switch (field) {
                        case 0: return control;
                        case 4: return ReadDICR();
                        default: throw new Exception("Unhandled DMA read at offset: " + offset.ToString("X"));
                    }

                default: throw new Exception("Unhandled DMA read at offset: " + offset.ToString("X"));

            }
        }
        public void StoreWord(UInt32 address, UInt32 value) {
            //uint offset = address - range.start;

            UInt32 ch = (address & 0x70) >> 4;       //Bits [7:5] for the channel number
            UInt32 field = (address & 0xf);              //Bits [3:0] for the field number

            switch (ch) {               //writing a field for a specific channel
                case 0x0:
                case 0x1:
                case 0x2:
                case 0x3:
                case 0x4:
                case 0x5:
                case 0x6:

                    switch (field) {
                        case 0x0: channels[ch].set_base_addr(value); break;
                        case 0x4: channels[ch].set_block_control(value); break;

                        case 0x8:
                        case 0xC: channels[ch].set_control(value); break;

                        default:  throw new Exception("Unhandled DMA write at offset: " + address.ToString("X") + " field: " + field + " val: " + value.ToString("X"));
                    }

                break;

                case 0x7:         //case 7 is general fields 
                    switch (field) {
                        case 0x0: control = value; break;
                        case 0x4: Write32_DICR(value); break;  //32-bit write to DICR
                        default: throw new Exception("Unhandled DMA write at offset: " + address.ToString("X") + " val: " + value.ToString("X"));
                    }
                    break;

                default: throw new Exception("Unhandled DMA write at offset: " + address.ToString("X") + " val: " + value.ToString("X"));
            }

            DMAChannel activeCH = is_active(address);  //Handle active DMA transfer (if any)
            if (activeCH != null) {
                if (activeCH.GetSync() == ((uint)DMAChannel.Sync.LinkedList)) {
                    HandleDMALinkedListBus(activeCH);
                } else {
                    HandleDMABus(activeCH);
                }
            }
        }

        public byte LoadByte(uint address) {
            //uint offset = address - range.start;
            uint reg = address & 0xFF;
            switch (reg) {
                case 0xF4: return (byte)(ReadDICR() & 0xFF);
                case 0xF5: return (byte)((ReadDICR() >> 8) & 0xFF);
                case 0xF6: return (byte)((ReadDICR() >> 16) & 0xFF);
                case 0xF7: return (byte)((ReadDICR() >> 24) & 0xFF);

                default: throw new Exception("Unhandled Read Byte from: " + address.ToString("x"));

            }
        }
        public void StoreByte(uint address, byte value) {
            //uint offset = address - range.start;
            uint reg = address & 0xFF;
            switch (reg) {
                case 0xF4: read_write = value; break;
                case 0xF5: force_irq = (uint)((value >> 7) & 1); break;   
                case 0xF6:
                    ch_irq_en = (byte)(value & 0x7f);    
                    master_enabled = (uint)((value >> 7) & 1);
                    break;

                case 0xF7: 
                    ch_irq_flags = (byte)(ch_irq_flags & (~(value & 0x7f)));
                    for (int i = 0; i < channels.Length; i++) {
                        channels[i].finished = ((ch_irq_flags >> i) & 1) == 1;
                    }
                    break;  

                default: throw new Exception("Unhandled Store Byte from: " + address.ToString("x"));
            }
        }

        private UInt32 ReadDICR() {
            UInt32 v = 0;
            /*for (int i = 0; i < channels.Length; i++) {
                if (channels[i].finished) {
                    if (((ch_irq_en >> i) & 1) == 1) {
                        ch_irq_flags = (byte)(ch_irq_flags | (1 << i));
                    }
                }
            }*/

            v = v | read_write;
            v = v | (force_irq << 15);
            v = v | (((UInt32)ch_irq_en) << 16);
            v = v | (master_enabled << 23);
            v = v | (((UInt32)ch_irq_flags) << 24);
            v = v | (IRQRequest() << 31);

            return v;
        }
        
        private void Write32_DICR(UInt32 value) {
            read_write = (byte)(value & 0x3f);
            force_irq = (value >> 15) & 1;
            ch_irq_en = (byte)((value >> 16) & 0x7f);
            master_enabled = (value >> 23) & 1;
            ch_irq_flags = (byte)(ch_irq_flags & (~((value >> 24) & 0x7f)));      //0x7F??

            for (int i = 0; i < channels.Length; i++) {
                channels[i].finished = ((ch_irq_flags >> i) & 1) == 1;
            }
        }
        public UInt32 IRQRequest() {
            if ((force_irq == 1) || ((master_enabled == 1) && ((ch_irq_en & ch_irq_flags) > 0))) {
                return 1;
            }
            else {
                return 0;
            }
        }

        internal ref DMAChannel is_active(uint offset) {
            UInt32 ch = (offset & 0x70) >> 4;
            if (ch<=6 && channels[ch].is_active()) {
                return ref channels[ch];
            }
            return ref reject;
        }
    }
}