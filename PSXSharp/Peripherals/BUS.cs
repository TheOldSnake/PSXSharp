using ABI.Windows.Devices.PointOfService;
using PSXSharp.Core.x64_Recompiler;
using PSXSharp.Peripherals.IO;
using PSXSharp.Peripherals.MDEC;
using PSXSharp.Peripherals.Timers;
using System;
using System.Collections.Generic;
using Windows.ApplicationModel.Activation;

namespace PSXSharp {
    public class BUS {      //Main BUS, connects the CPU to everything
        public BIOS BIOS;
        public MemoryControl MemoryControl;
        public RAM_SIZE RamSize;
        public CACHECONTROL CacheControl;
        public RAM RAM;
        public SPU SPU;
        public Expansion1 Expansion1;
        public Expansion2 Expansion2;
        public DMA DMA;
        public GPU GPU;
        public CD_ROM CDROM;
        public Timer0 Timer0;
        public Timer1 Timer1;
        public Timer2 Timer2;
        public JOY JOY_IO;
        public SIO1 SerialIO1;

        public Scratchpad Scratchpad;
        public MacroblockDecoder MDEC;
        private uint[] RegionMask = { 
                    // KUSEG: 2048MB
                       0xffffffff, 0xffffffff, 0xffffffff , 0xffffffff,
                    // KSEG0: 512MB
                       0x7fffffff,
                    // KSEG1: 512MB
                       0x1fffffff,
                    // KSEG2: 1024MB
                       0xffffffff, 0xffffffff
        };
        const double GPU_FACTOR = ((double)715909) / 451584;
        public bool debug = false;

        public uint BUS_Cycles = 0;
        Action Callback;

        public BUS(
            BIOS BIOS, RAM RAM, Scratchpad Scratchpad,
            CD_ROM CDROM, SPU SPU, DMA DMA, JOY JOY_IO,SIO1 SIO1, MemoryControl MemCtrl, 
            RAM_SIZE RamSize, CACHECONTROL CacheControl, Expansion1 Ex1, Expansion2 Ex2,
            Timer0 Timer0, Timer1 Timer1, Timer2 Timer2, MacroblockDecoder MDEC, GPU GPU) {
            this.BIOS = BIOS;
            this.RAM = RAM;
            this.Scratchpad = Scratchpad;
            this.CDROM = CDROM;
            this.SPU = SPU;
            this.DMA = DMA;
            this.JOY_IO = JOY_IO;
            this.SerialIO1 = SIO1;
            this.MemoryControl = MemCtrl;       //useless ?
            this.RamSize = RamSize;             //useless ?
            this.CacheControl = CacheControl;   //useless ?
            this.Expansion1 = Ex1;
            this.Expansion2 = Ex2;
            this.Timer0 = Timer0;
            this.Timer1 = Timer1;
            this.Timer2 = Timer2;
            this.MDEC = MDEC;
            this.GPU = GPU;
            Callback = DMAIRQ;
        }

        public uint LoadWord(uint address) {           
            uint physicalAddress = Mask(address);
            BUS_Cycles++;
            if (debug) Console.WriteLine("read32: " + address.ToString("X"));

            switch (physicalAddress) {
                case uint when RAM.Range.Contains(physicalAddress):  return RAM.LoadWord(physicalAddress);
                case uint when BIOS.range.Contains(physicalAddress): return BIOS.LoadWord(physicalAddress);
                case uint when IRQ_CONTROL.range.Contains(physicalAddress): return IRQ_CONTROL.Read(physicalAddress);
                case uint when DMA.range.Contains(physicalAddress): return DMA.ReadWord(physicalAddress);
                case uint when GPU.Range.Contains(physicalAddress): return GPU.LoadWord(physicalAddress);
                case uint when SPU.range.Contains(physicalAddress): return SPU.LoadWord(physicalAddress);
                case uint when Timer0.Range.Contains(physicalAddress): return Timer0.Read(physicalAddress);    
                case uint when Timer1.Range.Contains(physicalAddress): return Timer1.Read(physicalAddress);
                case uint when Timer2.Range.Contains(physicalAddress): return Timer2.Read(physicalAddress);
                case uint when Scratchpad.range.Contains(physicalAddress): return Scratchpad.LoadWord(physicalAddress);
                case uint when JOY_IO.Range.Contains(physicalAddress): return JOY_IO.LoadWord(physicalAddress);
                case uint when SerialIO1.Range.Contains(physicalAddress): return SerialIO1.LoadWord(physicalAddress);
                case uint when MemoryControl.range.Contains(physicalAddress): return MemoryControl.Read(physicalAddress);
                case uint when MDEC.range.Contains(physicalAddress): return MDEC.Read(physicalAddress);
                case uint when RamSize.range.Contains(physicalAddress): return RamSize.LoadWord();
                case uint when address >= 0x1F800400 && address <= 0x1F800400 + 0xC00: return 0xFFFFFFFF;
                case uint when address >= 0x1F801024 && address <= 0x1F801024 + 0x01C: return 0xFFFFFFFF;
                case uint when address >= 0x1F801064 && address <= 0x1F801064 + 0x00C: return 0xFFFFFFFF;
                case uint when address >= 0x1F801078 && address <= 0x1F801078 + 0x008: return 0xFFFFFFFF;

                default: throw new Exception("Unhandled LoadWord from: " + address.ToString("X"));
            }
        }

        public void StoreWord(uint address,uint value) {
            uint physicalAddress = Mask(address);
            BUS_Cycles++;
            if (debug) Console.WriteLine("write32: " + address.ToString("X"));

            switch (physicalAddress) {
                case uint when RAM.Range.Contains(physicalAddress): RAM.StoreWord(physicalAddress, value); break;
                case uint when RamSize.range.Contains(physicalAddress): RamSize.StoreWord(value); break;
                case uint when MemoryControl.range.Contains(physicalAddress): MemoryControl.Write(physicalAddress, value); break;
                case uint when IRQ_CONTROL.range.Contains(physicalAddress): IRQ_CONTROL.Write(physicalAddress, (ushort)value); break; //Cast? could be wrong
                case uint when GPU.Range.Contains(physicalAddress): GPU.StoreWord(physicalAddress, value); break;
                case uint when CacheControl.Range.Contains(physicalAddress): CacheControl.WriteWord(address, value); break;
                case uint when Timer0.Range.Contains(physicalAddress): Timer0.Write(physicalAddress, value); break;    
                case uint when Timer1.Range.Contains(physicalAddress): Timer1.Write(physicalAddress, value); break;
                case uint when Timer2.Range.Contains(physicalAddress): Timer2.Write(physicalAddress, value); break;
                case uint when Scratchpad.range.Contains(physicalAddress): Scratchpad.StoreWord(physicalAddress, value); break;
                case uint when MDEC.range.Contains(physicalAddress): MDEC.Write(physicalAddress, value); break;
                case uint when DMA.range.Contains(physicalAddress):
                    DMA.StoreWord(physicalAddress, value);
                    DMAChannel activeCH = DMA.is_active(physicalAddress);  //Handle active DMA transfer (if any)
                    if (activeCH != null) {
                        if (activeCH.GetSync() == ((uint)DMAChannel.Sync.LinkedList)) {
                            HandleDMALinkedList(ref activeCH);
                        } else {
                            HandleDMA(ref activeCH);
                        }
                    }
                    break;
                case uint when address >= 0x1F800400 && address <= 0x1F800400 + 0xC00: break;
                case uint when address >= 0x1F801024 && address <= 0x1F801024 + 0x01C: break;
                case uint when address >= 0x1F801064 && address <= 0x1F801064 + 0x00C: break;
                case uint when address >= 0x1F801078 && address <= 0x1F801078 + 0x008: break;

                default: throw new Exception("Unhandled StoreWord to: " + address.ToString("X")); 
            }
        }

        public ushort LoadHalf(uint address) {
            uint physicalAddress = Mask(address);
            BUS_Cycles++;
            if(debug) Console.WriteLine("read16: " + address.ToString("X"));

            switch (physicalAddress) {
                case uint when RAM.Range.Contains(physicalAddress): return RAM.LoadHalf(physicalAddress);
                case uint when BIOS.range.Contains(physicalAddress): return BIOS.LoadHalf(physicalAddress);
                case uint when SPU.range.Contains(physicalAddress): return SPU.LoadHalf(physicalAddress);
                case uint when IRQ_CONTROL.range.Contains(physicalAddress): return (ushort)IRQ_CONTROL.Read(physicalAddress);
                case uint when DMA.range.Contains(physicalAddress): return (ushort)DMA.ReadWord(physicalAddress); //DMA only 32-bits?
                case uint when Timer0.Range.Contains(physicalAddress): return (ushort)Timer0.Read(physicalAddress);
                case uint when Timer1.Range.Contains(physicalAddress): return (ushort)Timer1.Read(physicalAddress);
                case uint when Timer2.Range.Contains(physicalAddress): return (ushort)Timer2.Read(physicalAddress);
                case uint when JOY_IO.Range.Contains(physicalAddress): return JOY_IO.LoadHalf(physicalAddress);
                case uint when SerialIO1.Range.Contains(physicalAddress): return SerialIO1.LoadHalf(physicalAddress);
                case uint when Scratchpad.range.Contains(physicalAddress): return Scratchpad.LoadHalf(physicalAddress);
                case uint when MemoryControl.range.Contains(physicalAddress): return (ushort)MemoryControl.Read(physicalAddress);
                case uint when address >= 0x1F800400 && address <= 0x1F800400 + 0xC00: return 0xFFFF;
                case uint when address >= 0x1F801024 && address <= 0x1F801024 + 0x01C: return 0xFFFF;
                case uint when address >= 0x1F801064 && address <= 0x1F801064 + 0x00C: return 0xFFFF;
                case uint when address >= 0x1F801078 && address <= 0x1F801078 + 0x008: return 0xFFFF;


                default: throw new Exception("Unhandled LoadHalf from: " + address.ToString("X"));
            }    
        }

        public void StoreHalf(uint address, ushort value) {
            uint physicalAddress = Mask(address);
            BUS_Cycles++;
            if (debug) Console.WriteLine("write16: " + address.ToString("X"));

            switch (physicalAddress) {
                case uint when RAM.Range.Contains(physicalAddress): RAM.StoreHalf(physicalAddress, value); break;
                case uint when SPU.range.Contains(physicalAddress): SPU.StoreHalf(physicalAddress, value); break;
                case uint when Timer0.Range.Contains(physicalAddress): Timer0.Write(physicalAddress, value); break;
                case uint when Timer1.Range.Contains(physicalAddress): Timer1.Write(physicalAddress, value); break;
                case uint when Timer2.Range.Contains(physicalAddress): Timer2.Write(physicalAddress, value); break;
                case uint when IRQ_CONTROL.range.Contains(physicalAddress): IRQ_CONTROL.Write(physicalAddress, value); break;
                case uint when JOY_IO.Range.Contains(physicalAddress): JOY_IO.StoreHalf(physicalAddress, value); break;
                case uint when SerialIO1.Range.Contains(physicalAddress): SerialIO1.StoreHalf(physicalAddress, value); break;
                case uint when Scratchpad.range.Contains(physicalAddress): Scratchpad.StoreHalf(physicalAddress, value); break;
                case uint when MemoryControl.range.Contains(physicalAddress): MemoryControl.Write(physicalAddress, value); break;
                case uint when DMA.range.Contains(physicalAddress):
                    DMA.StoreWord(physicalAddress, value);
                    DMAChannel activeCH = DMA.is_active(physicalAddress);  //Handle active DMA transfer (if any)
                    if (activeCH != null) {
                        HandleDMA(ref activeCH);
                    }
                    break;
                case 0x1f802082: Console.WriteLine("Redux-Expansion Exit code: " + value.ToString("x")); break;
                case uint when address >= 0x1F800400 && address <= 0x1F800400 + 0xC00: break;
                case uint when address >= 0x1F801024 && address <= 0x1F801024 + 0x01C: break;
                case uint when address >= 0x1F801064 && address <= 0x1F801064 + 0x00C: break;
                case uint when address >= 0x1F801078 && address <= 0x1F801078 + 0x008: break;

                default: throw new Exception("Unhandled StoreHalf to: " + address.ToString("X")); return;
            }
        }

        public byte LoadByte(uint address) {
            uint physicalAddress = Mask(address);
            BUS_Cycles++;
            if (debug) Console.WriteLine("read8: " + address.ToString("X"));

            switch (physicalAddress) {
                case uint when RAM.Range.Contains(physicalAddress):  return RAM.LoadByte(physicalAddress);
                case uint when BIOS.range.Contains(physicalAddress): return BIOS.LoadByte(physicalAddress);
                case uint when CDROM.range.Contains(physicalAddress): return CDROM.LoadByte(physicalAddress);
                case uint when DMA.range.Contains(physicalAddress): return DMA.LoadByte(physicalAddress);
                case uint when MemoryControl.range.Contains(physicalAddress): return (byte)MemoryControl.Read(physicalAddress);
                case uint when Scratchpad.range.Contains(physicalAddress): return Scratchpad.LoadByte(physicalAddress);
                case uint when JOY_IO.Range.Contains(physicalAddress): return JOY_IO.LoadByte(physicalAddress);
                case uint when SerialIO1.Range.Contains(physicalAddress): return SerialIO1.LoadByte(physicalAddress);
                case uint when Expansion1.range.Contains(physicalAddress):   
                case uint when Expansion2.range.Contains(physicalAddress): return 0xFF;   //Ignore Expansions 1 and 2 
                case uint when address >= 0x1F800400 && address <= 0x1F800400 + 0xC00: return 0xFF;
                case uint when address >= 0x1F801024 && address <= 0x1F801024 + 0x01C: return 0xFF;
                case uint when address >= 0x1F801064 && address <= 0x1F801064 + 0x00C: return 0xFF;
                case uint when address >= 0x1F801078 && address <= 0x1F801078 + 0x008: return 0xFF;

                default: throw new Exception("Unhandled LoadByte from: " + address.ToString("X"));

            }
        }

        public void StoreByte(uint address, byte value) {
            uint physicalAddress = Mask(address);
            BUS_Cycles++;
            if (debug) Console.WriteLine("write8: " + address.ToString("X"));

            switch (physicalAddress) {
                case uint when RAM.Range.Contains(physicalAddress): RAM.StoreByte(physicalAddress, value); break;
                case uint when Scratchpad.range.Contains(physicalAddress): Scratchpad.StoreByte(physicalAddress, value); break;
                case uint when CDROM.range.Contains(physicalAddress): CDROM.StoreByte(physicalAddress, value); break;
                case uint when DMA.range.Contains(physicalAddress): DMA.StoreByte(physicalAddress, value); break;
                case uint when JOY_IO.Range.Contains(physicalAddress): JOY_IO.StoreByte(physicalAddress, value); break;
                case uint when SerialIO1.Range.Contains(physicalAddress): SerialIO1.StoreByte(physicalAddress, value); break;
                case uint when MemoryControl.range.Contains(physicalAddress): MemoryControl.Write(physicalAddress, value); break;
                case uint when Expansion1.range.Contains(physicalAddress):
                case uint when Expansion2.range.Contains(physicalAddress): break;   //Ignore Expansions 1 and 2
                case uint when address >= 0x1F800400 && address <= 0x1F800400 + 0xC00: break;
                case uint when address >= 0x1F801024 && address <= 0x1F801024 + 0x01C: break;
                case uint when address >= 0x1F801064 && address <= 0x1F801064 + 0x00C: break;
                case uint when address >= 0x1F801078 && address <= 0x1F801078 + 0x008: break;

                default: throw new Exception("Unhandled StoreByte to: " + address.ToString("X")); 
            }           
        }

        public uint GetBusCycles() {
            uint numberOfCycles = BUS_Cycles;
            BUS_Cycles = 0;
            return 0;
        }

        public uint Mask(uint address) { 
            uint index = address >> 29;
            uint physical_address = address & RegionMask[index];
            return physical_address;
        }

        private void HandleDMALinkedList(ref DMAChannel activeCH) {     
            DMAChannel ch = activeCH;
         
            if (ch.get_direction() == ((uint)DMAChannel.Direction.ToRam)) {
                throw new Exception("Invalid direction for LinkedList transfer");
            }
            if (ch.get_portnum() != 2) {
                throw new Exception("Attempt to use LinkedList mode in DMA port: " + ch.get_portnum());
            }

            uint address = ch.read_base_addr() & 0x1ffffc;
            int LinkedListMax = 0xFFFF;     //A hacky way to get out of infinite list transfares

            while (LinkedListMax-- > 0) {
                uint header = RAM.LoadWord(address);
                uint num_of_words = header >> 24;

                while (num_of_words > 0) {
                    address = (address + 4) & 0x1ffffc;

                    uint command = RAM.LoadWord(address);
                    GPU.WriteGP0(command);
                    num_of_words -= 1;

                }
                if ((header & 0x800000) != 0) {
                    break;
                }
                address = header & 0x1ffffc;
            }
            ch.done();

            if (((DMA.ch_irq_en >> (int)ch.get_portnum()) & 1) == 1) {
                DMA.ch_irq_flags |= (byte)(1 << (int)ch.get_portnum());
            }

            if (DMA.IRQRequest() == 1) {
                //IRQ_CONTROL.IRQsignal(3);   //Instant IRQ may cause problems
                Scheduler.ScheduleEvent(0, Callback, Event.DMA);
            }
            
        }

        private void HandleDMA(ref DMAChannel activeCH) {
            DMAChannel ch = activeCH;
            if (activeCH.GetSync() == ((uint)DMAChannel.Sync.LinkedList)) {
                HandleDMALinkedList(ref ch);
                return;
            }


            int step;
            if (ch.get_step() == ((uint)DMAChannel.Step.Increment)) {
                step = 4;
            }
            else {
                step = -4;
            }

            uint base_address = ch.read_base_addr();
            uint? transfer_size = ch.get_transfer_size();

            if (transfer_size == null) {
                throw new Exception("transfer size is null, LinkedList mode?");
            }

            while (transfer_size > 0) {
                uint current_address = base_address & 0x1ffffc;

                if (ch.get_direction() == ((uint)DMAChannel.Direction.FromRam)) {

                    uint data = RAM.LoadWord(current_address);

                    switch (ch.get_portnum()) {
                        case 0: MDEC.CommandAndParameters(data); break;   //MDECin  (RAM to MDEC)
                        case 2: GPU.WriteGP0(data); break;
                        case 4: SPU.DMAtoSPU(data);  break;
                        default: throw new Exception("Unhandled DMA destination port: " + ch.get_portnum());
                    }
                } else {
                    
                    switch (ch.get_portnum()) {
                        case 1:
                            uint w = MDEC.ReadCurrentMacroblock();                           
                            RAM.StoreWord(current_address, w);
                            break;

                        case 2:  //GPU
                            uint data = GPU.CurrentTransfare.ReadWord();
                            if (GPU.CurrentTransfare.DataEnd) {
                                GPU.CurrentTransfare = null;
                                GPU.currentState = GPU.GPUState.Idle;
                            }
                            RAM.StoreWord(current_address, data);
                            break;

                        case 3: RAM.StoreWord(current_address, CDROM.DataController.ReadWord()); break;  //CD-ROM
                        case 4: RAM.StoreWord(current_address, SPU.SPUtoDMA()); break;                    //SPU

                        case 6:
                            if (transfer_size == 1) {
                                RAM.StoreWord(current_address, 0xffffff);
                            } else {
                                RAM.StoreWord(current_address, (base_address - 4) & 0x1fffff);
                            }
                            break;

                        default: throw new Exception("Unhandled DMA copy port: " + ch.get_portnum());

                    }
                }

                base_address = (uint)(base_address + step);
                transfer_size -= 1;
            }

            ch.done();

            //DMA IRQ 
            if (((DMA.ch_irq_en >> (int)ch.get_portnum()) & 1) == 1) {
                DMA.ch_irq_flags |= (byte)(1 << (int)ch.get_portnum());
            }

            if (DMA.IRQRequest() == 1) {
                //IRQ_CONTROL.IRQsignal(3);   //Instant IRQ may cause problems
                Scheduler.ScheduleEvent(0, Callback, Event.DMA);
            };
        }

        public void Tick(int cycles) {
            //Timer0.SystemClockTick(cycles);
            //Timer1.SystemClockTick(cycles);
            //Timer2.SystemClockTick(cycles);
            //SPU.SPU_Tick(cycles);
            //GPU.Tick(cycles * GPU_FACTOR);
            //CDROM.tick(cycles);
            // JOY_IO.Tick(cycles);
        }

        public void DMAIRQ() {
            IRQ_CONTROL.IRQsignal(3);
        }

    }
}

