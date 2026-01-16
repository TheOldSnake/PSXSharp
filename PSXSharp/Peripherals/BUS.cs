using PSXSharp.Core;
using PSXSharp.Peripherals.IO;
using PSXSharp.Peripherals.MDEC;
using PSXSharp.Peripherals.Timers;
using System;

namespace PSXSharp {
    public unsafe class BUS {      //Main BUS, connects the CPU to everything
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
            //KUSEG: 2048MB
            0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF,
            //KSEG0: 512MB
            0x7FFFFFFF,
            //KSEG1: 512MB
            0x1FFFFFFF,
            //KSEG2: 1024MB
            0xFFFFFFFF, 0xFFFFFFFF
        };

        const double GPU_FACTOR = ((double)715909) / 451584;
        public bool debug = false;

        public uint BUS_Cycles = 0;
        Action Callback;

        //Paging constants for a 64-KB pages
        private const int PAGE_SHIFT = 16;                     //16-bits for 64-KB
        private const int PAGE_COUNT = 1 << (32 - PAGE_SHIFT); //Total number of pages in a 32-bit address space
        private const int PAGE_SIZE = 1 << PAGE_SHIFT;         //Size of each page in bytes
        private const int PAGE_OFFSET_MASK = PAGE_SIZE - 1;    //Mask to extract offset within a page

        //Size (in bytes) of a full page table where each entry is a pointer-sized value
        private readonly uint PageTableSizeBytes = (uint)(PAGE_SIZE * nuint.Size);

        //Pointers to pointers, certified C moment.
        private readonly byte** ReadPageTable;
        private readonly byte** WritePageTable;

        //Scratchpad is only accessable via KUSEG and KSEG0
        public bool IsScratchPad(uint page, uint offset) => (page == 0x1F80 || page == 0x9F80) && offset < 0x400;

        public BUS(
            BIOS BIOS, RAM RAM, Scratchpad Scratchpad,
            CD_ROM CDROM, SPU SPU, JOY JOY_IO, SIO1 SIO1, MemoryControl MemCtrl,
            RAM_SIZE RamSize, CACHECONTROL CacheControl, Expansion1 Ex1, Expansion2 Ex2,
            Timer0 Timer0, Timer1 Timer1, Timer2 Timer2, MacroblockDecoder MDEC, GPU GPU) {
            this.BIOS = BIOS;
            this.RAM = RAM;
            this.Scratchpad = Scratchpad;
            this.CDROM = CDROM;
            this.SPU = SPU;
            this.DMA = new DMA(HandleDMA, HandleDMALinkedList);
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

            ReadPageTable = (byte**)NativeMemoryManager.AllocateNativeMemory(PageTableSizeBytes);
            WritePageTable = (byte**)NativeMemoryManager.AllocateNativeMemory(PageTableSizeBytes);
            InitlizeFastmem();
        }

        public void InitlizeFastmem() {
            if (ReadPageTable == null || WritePageTable == null) {
                throw new Exception("Page table was not allocated");
            }

            //Initialize all pages to null 
            NativeMemoryManager.FillNativeMemory(ReadPageTable, PageTableSizeBytes, 0);
            NativeMemoryManager.FillNativeMemory(WritePageTable, PageTableSizeBytes, 0);

            //Map BIOS pages (read only)
            MapPages(BIOS.BASE_ADDRESS, BIOS.SIZE, BIOS.NativeAddress, ReadPageTable);

            //Map RAM pages (read/write)
            //The actual size is 2MB but since it's mirrored 4 times (contiguous),
            //we map it 4 times to the same pages
            for (uint i = 0; i < 4; i++) {
                MapPages(RAM.BASE_ADDRESS + (i * RAM.SIZE), RAM.SIZE, RAM.NativeAddress, ReadPageTable);
                MapPages(RAM.BASE_ADDRESS + (i * RAM.SIZE), RAM.SIZE, RAM.NativeAddress, WritePageTable);
            }
        }

        public void MapPages(uint baseGuestAddress, uint length, byte* baseNativeAddress, byte** pageTable) {
            uint maskesAddress = Mask(baseGuestAddress);
            uint startPage = maskesAddress >> PAGE_SHIFT;
            uint numberOfPages = length / PAGE_SIZE;
            uint endPage = startPage + numberOfPages;

            if (length % PAGE_SIZE != 0) {
                throw new Exception($"Cannot map {baseGuestAddress} with length: {length} to pages of size: {PAGE_SIZE}");
            }

            string access = pageTable == ReadPageTable ? "Read" : "Write";
            Console.WriteLine($"[Fastmem] Mapping guest: 0x{maskesAddress:X8} to host: 0x{(ulong)baseNativeAddress:X16} access = {access}");

            const uint KUSEG  = 0x0000;
            const uint KUSEG0 = 0x8000;
            const uint KUSEG1 = 0xA000;
            uint pageIndex = 0;

            for (uint i = startPage; i < endPage; i++) {
                pageTable[i + KUSEG]  = baseNativeAddress + (pageIndex * PAGE_SIZE);
                pageTable[i + KUSEG0] = baseNativeAddress + (pageIndex * PAGE_SIZE);
                pageTable[i + KUSEG1] = baseNativeAddress + (pageIndex * PAGE_SIZE);
                pageIndex++;
            }
        }

        public uint ReadWord(uint virtualAddress) {
            uint page = virtualAddress >> PAGE_SHIFT;
            byte* nativeAddress = ReadPageTable[page];
            uint inPageOffset = virtualAddress & PAGE_OFFSET_MASK;

            if (nativeAddress != null) {
                return *(uint*)(nativeAddress + inPageOffset);
            }

            if (IsScratchPad(page, inPageOffset)) {
                return *(uint*)(Scratchpad.NativeAddress + inPageOffset);
            }

            return ReadWordIO(virtualAddress);
        }

        public ushort ReadHalf(uint virtualAddress) {
            uint page = virtualAddress >> PAGE_SHIFT;
            byte* nativeAddress = ReadPageTable[page];
            uint inPageOffset = virtualAddress & PAGE_OFFSET_MASK;

            if (nativeAddress != null) {
                return *(ushort*)(nativeAddress + inPageOffset);
            }

            if (IsScratchPad(page, inPageOffset)) {
                return *(ushort*)(Scratchpad.NativeAddress + inPageOffset);
            }

            return ReadHalfIO(virtualAddress);
        }

        public byte ReadByte(uint virtualAddress) {
            uint page = virtualAddress >> PAGE_SHIFT;
            byte* nativeAddress = ReadPageTable[page];
            uint inPageOffset = virtualAddress & PAGE_OFFSET_MASK;

            if (nativeAddress != null) {
                return *(nativeAddress + inPageOffset);
            }

            if (IsScratchPad(page, inPageOffset)) {
                return *(Scratchpad.NativeAddress + inPageOffset);
            }

            return ReadByteIO(virtualAddress);
        }

        public void WriteWord(uint virtualAddress, uint value) {
            uint page = virtualAddress >> PAGE_SHIFT;
            byte* nativeAddress = WritePageTable[page];
            uint inPageOffset = virtualAddress & PAGE_OFFSET_MASK;

            if (nativeAddress != null) {
                *(uint*)(nativeAddress + inPageOffset) = value;    //Should I invalidate the JIT block..?
            } else if (IsScratchPad(page, inPageOffset)) {
                *(uint*)(Scratchpad.NativeAddress + inPageOffset) = value;

            } else {
                WriteWordIO(virtualAddress, value);
            }
        }

        public void WriteHalf(uint virtualAddress, ushort value) {
            uint page = virtualAddress >> PAGE_SHIFT;
            byte* nativeAddress = WritePageTable[page];
            uint inPageOffset = virtualAddress & PAGE_OFFSET_MASK;

            if (nativeAddress != null) {
                *(ushort*)(nativeAddress + inPageOffset) = value;   //Should I invalidate the JIT block..?
            } else if (IsScratchPad(page, inPageOffset)) {
                *(ushort*)(Scratchpad.NativeAddress + inPageOffset) = value;
            } else {
                WriteHalfIO(virtualAddress, value);
            }
        }

        public void WriteByte(uint virtualAddress, byte value) {
            uint page = virtualAddress >> PAGE_SHIFT;
            byte* nativeAddress = WritePageTable[page];
            uint inPageOffset = virtualAddress & PAGE_OFFSET_MASK;

            if (nativeAddress != null) {
                *(nativeAddress + inPageOffset) = value;           //Should I invalidate the JIT block..?
            } else if (IsScratchPad(page, inPageOffset)) {
                *(Scratchpad.NativeAddress + inPageOffset) = value;
            } else {
                WriteByteIO(virtualAddress, value);
            }
        }

        public uint ReadWordIO(uint virtualAddress) {
            uint physicalAddress = Mask(virtualAddress);

            switch (physicalAddress) {
                case uint when IRQ_CONTROL.range.Contains(physicalAddress): return IRQ_CONTROL.Read(physicalAddress);
                case uint when DMA.range.Contains(physicalAddress): return DMA.ReadWord(physicalAddress);
                case uint when GPU.Range.Contains(physicalAddress): return GPU.LoadWord(physicalAddress);
                case uint when SPU.range.Contains(physicalAddress): return SPU.LoadWord(physicalAddress);
                case uint when Timer0.Range.Contains(physicalAddress): return Timer0.Read(physicalAddress);
                case uint when Timer1.Range.Contains(physicalAddress): return Timer1.Read(physicalAddress);
                case uint when Timer2.Range.Contains(physicalAddress): return Timer2.Read(physicalAddress);
                case uint when JOY_IO.Range.Contains(physicalAddress): return JOY_IO.LoadWord(physicalAddress);
                case uint when SerialIO1.Range.Contains(physicalAddress): return SerialIO1.LoadWord(physicalAddress);
                case uint when MemoryControl.range.Contains(physicalAddress): return MemoryControl.Read(physicalAddress);
                case uint when MDEC.range.Contains(physicalAddress): return MDEC.Read(physicalAddress);
                case uint when RamSize.range.Contains(physicalAddress): return RamSize.LoadWord();
                case uint when physicalAddress >= 0x1F800400 && physicalAddress <= 0x1F800400 + 0xC00: return 0xFFFFFFFF;
                case uint when physicalAddress >= 0x1F801024 && physicalAddress <= 0x1F801024 + 0x01C: return 0xFFFFFFFF;
                case uint when physicalAddress >= 0x1F801064 && physicalAddress <= 0x1F801064 + 0x00C: return 0xFFFFFFFF;
                case uint when physicalAddress >= 0x1F801078 && physicalAddress <= 0x1F801078 + 0x008: return 0xFFFFFFFF;

                default: throw new Exception($"Unhandled LoadWord from: {physicalAddress:X8}");
            }
        }

        public ushort ReadHalfIO(uint virtualAddress) {
            uint physicalAddress = Mask(virtualAddress);

            switch (physicalAddress) {
                case uint when SPU.range.Contains(physicalAddress): return SPU.LoadHalf(physicalAddress);
                case uint when IRQ_CONTROL.range.Contains(physicalAddress): return (ushort)IRQ_CONTROL.Read(physicalAddress);
                case uint when DMA.range.Contains(physicalAddress): return (ushort)DMA.ReadWord(physicalAddress); //DMA only 32-bits?
                case uint when Timer0.Range.Contains(physicalAddress): return (ushort)Timer0.Read(physicalAddress);
                case uint when Timer1.Range.Contains(physicalAddress): return (ushort)Timer1.Read(physicalAddress);
                case uint when Timer2.Range.Contains(physicalAddress): return (ushort)Timer2.Read(physicalAddress);
                case uint when JOY_IO.Range.Contains(physicalAddress): return JOY_IO.LoadHalf(physicalAddress);
                case uint when SerialIO1.Range.Contains(physicalAddress): return SerialIO1.LoadHalf(physicalAddress);
                case uint when MemoryControl.range.Contains(physicalAddress): return (ushort)MemoryControl.Read(physicalAddress);
                case uint when physicalAddress >= 0x1F800400 && physicalAddress <= 0x1F800400 + 0xC00: return 0xFFFF;
                case uint when physicalAddress >= 0x1F801024 && physicalAddress <= 0x1F801024 + 0x01C: return 0xFFFF;
                case uint when physicalAddress >= 0x1F801064 && physicalAddress <= 0x1F801064 + 0x00C: return 0xFFFF;
                case uint when physicalAddress >= 0x1F801078 && physicalAddress <= 0x1F801078 + 0x008: return 0xFFFF;

                default: throw new Exception($"Unhandled LoadHalf from: {physicalAddress:X8}");
            }
        }

        public byte ReadByteIO(uint virtualAddress) {
            uint physicalAddress = Mask(virtualAddress);
            switch (physicalAddress) {
                case uint when CDROM.range.Contains(physicalAddress): return CDROM.LoadByte(physicalAddress);
                case uint when DMA.range.Contains(physicalAddress): return DMA.LoadByte(physicalAddress);
                case uint when MemoryControl.range.Contains(physicalAddress): return (byte)MemoryControl.Read(physicalAddress);
                case uint when JOY_IO.Range.Contains(physicalAddress): return JOY_IO.LoadByte(physicalAddress);
                case uint when SerialIO1.Range.Contains(physicalAddress): return SerialIO1.LoadByte(physicalAddress);
                case uint when Expansion1.range.Contains(physicalAddress):
                case uint when Expansion2.range.Contains(physicalAddress): return 0xFF;   //Ignore Expansions 1 and 2 
                case uint when physicalAddress >= 0x1F800400 && physicalAddress <= 0x1F800400 + 0xC00: return 0xFF;
                case uint when physicalAddress >= 0x1F801024 && physicalAddress <= 0x1F801024 + 0x01C: return 0xFF;
                case uint when physicalAddress >= 0x1F801064 && physicalAddress <= 0x1F801064 + 0x00C: return 0xFF;
                case uint when physicalAddress >= 0x1F801078 && physicalAddress <= 0x1F801078 + 0x008: return 0xFF;

                default: throw new Exception($"Unhandled LoadByte from: {physicalAddress:X8}");
            }
        }

        public void WriteWordIO(uint address,uint value) {
            uint physicalAddress = Mask(address);

            switch (physicalAddress) {
                case uint when RamSize.range.Contains(physicalAddress): RamSize.StoreWord(value); break;
                case uint when MemoryControl.range.Contains(physicalAddress): MemoryControl.Write(physicalAddress, value); break;
                case uint when IRQ_CONTROL.range.Contains(physicalAddress): IRQ_CONTROL.Write(physicalAddress, (ushort)value); break; //Cast? could be wrong
                case uint when GPU.Range.Contains(physicalAddress): GPU.StoreWord(physicalAddress, value); break;
                case uint when CacheControl.Range.Contains(physicalAddress): CacheControl.WriteWord(address, value); break;
                case uint when Timer0.Range.Contains(physicalAddress): Timer0.Write(physicalAddress, value); break;    
                case uint when Timer1.Range.Contains(physicalAddress): Timer1.Write(physicalAddress, value); break;
                case uint when Timer2.Range.Contains(physicalAddress): Timer2.Write(physicalAddress, value); break;
                case uint when MDEC.range.Contains(physicalAddress): MDEC.Write(physicalAddress, value); break;
                case uint when DMA.range.Contains(physicalAddress): DMA.StoreWord(physicalAddress, value); break;
                case uint when physicalAddress >= 0x1F800400 && physicalAddress <= 0x1F800400 + 0xC00: break;
                case uint when physicalAddress >= 0x1F801024 && physicalAddress <= 0x1F801024 + 0x01C: break;
                case uint when physicalAddress >= 0x1F801064 && physicalAddress <= 0x1F801064 + 0x00C: break;
                case uint when physicalAddress >= 0x1F801078 && physicalAddress <= 0x1F801078 + 0x008: break;

                default: throw new Exception($"Unhandled StoreWord to: {physicalAddress:X8}"); 
            }
        }

        public void WriteHalfIO(uint address, ushort value) {
            uint physicalAddress = Mask(address);
            switch (physicalAddress) {
                case uint when SPU.range.Contains(physicalAddress): SPU.StoreHalf(physicalAddress, value); break;
                case uint when Timer0.Range.Contains(physicalAddress): Timer0.Write(physicalAddress, value); break;
                case uint when Timer1.Range.Contains(physicalAddress): Timer1.Write(physicalAddress, value); break;
                case uint when Timer2.Range.Contains(physicalAddress): Timer2.Write(physicalAddress, value); break;
                case uint when IRQ_CONTROL.range.Contains(physicalAddress): IRQ_CONTROL.Write(physicalAddress, value); break;
                case uint when JOY_IO.Range.Contains(physicalAddress): JOY_IO.StoreHalf(physicalAddress, value); break;
                case uint when SerialIO1.Range.Contains(physicalAddress): SerialIO1.StoreHalf(physicalAddress, value); break;
                case uint when MemoryControl.range.Contains(physicalAddress): MemoryControl.Write(physicalAddress, value); break;
                case uint when DMA.range.Contains(physicalAddress): DMA.StoreWord(physicalAddress, value); break;
                case 0x1f802082: Console.WriteLine("Redux-Expansion Exit code: " + value.ToString("x")); break;
                case uint when physicalAddress >= 0x1F800400 && physicalAddress <= 0x1F800400 + 0xC00: break;
                case uint when physicalAddress >= 0x1F801024 && physicalAddress <= 0x1F801024 + 0x01C: break;
                case uint when physicalAddress >= 0x1F801064 && physicalAddress <= 0x1F801064 + 0x00C: break;
                case uint when physicalAddress >= 0x1F801078 && physicalAddress <= 0x1F801078 + 0x008: break;

                default: throw new Exception($"Unhandled StoreHalf to: {physicalAddress:X8}");
            }
        }

        public void WriteByteIO(uint address, byte value) {
            uint physicalAddress = Mask(address);
            switch (physicalAddress) {
                case uint when CDROM.range.Contains(physicalAddress): CDROM.StoreByte(physicalAddress, value); break;
                case uint when DMA.range.Contains(physicalAddress): DMA.StoreByte(physicalAddress, value); break;
                case uint when JOY_IO.Range.Contains(physicalAddress): JOY_IO.StoreByte(physicalAddress, value); break;
                case uint when SerialIO1.Range.Contains(physicalAddress): SerialIO1.StoreByte(physicalAddress, value); break;
                case uint when MemoryControl.range.Contains(physicalAddress): MemoryControl.Write(physicalAddress, value); break;
                case uint when Expansion1.range.Contains(physicalAddress):
                case uint when Expansion2.range.Contains(physicalAddress): break;   //Ignore Expansions 1 and 2
                case uint when physicalAddress >= 0x1F800400 && physicalAddress <= 0x1F800400 + 0xC00: break;
                case uint when physicalAddress >= 0x1F801024 && physicalAddress <= 0x1F801024 + 0x01C: break;
                case uint when physicalAddress >= 0x1F801064 && physicalAddress <= 0x1F801064 + 0x00C: break;
                case uint when physicalAddress >= 0x1F801078 && physicalAddress <= 0x1F801078 + 0x008: break;

                default: throw new Exception($"Unhandled StoreByte to: {physicalAddress:X8}"); 
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

        private void HandleDMALinkedList(DMAChannel activeCH) {     
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
                uint header = RAM.Read<uint>(address);
                uint num_of_words = header >> 24;

                while (num_of_words > 0) {
                    address = (address + 4) & 0x1ffffc;

                    uint command = RAM.Read<uint>(address);
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

        private void HandleDMA(DMAChannel activeCH) {
            DMAChannel ch = activeCH;
            if (activeCH.GetSync() == ((uint)DMAChannel.Sync.LinkedList)) {
                HandleDMALinkedList(ch);
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

                    uint data = RAM.Read<uint>(current_address);

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
                            RAM.Write<uint>(current_address, w);
                            break;

                        case 2:  //GPU
                            uint data = GPU.CurrentTransfare.ReadWord();
                            if (GPU.CurrentTransfare.DataEnd) {
                                GPU.CurrentTransfare = null;
                                GPU.currentState = GPU.GPUState.Idle;
                            }
                            RAM.Write<uint>(current_address, data);
                            break;

                        case 3: RAM.Write<uint>(current_address, CDROM.DataController.ReadWord()); break;  //CD-ROM
                        case 4: RAM.Write<uint>(current_address, SPU.SPUtoDMA()); break;                    //SPU

                        case 6:
                            if (transfer_size == 1) {
                                RAM.Write<uint>(current_address, 0xffffff);
                            } else {
                                RAM.Write<uint>(current_address, (base_address - 4) & 0x1fffff);
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

        public void DMAIRQ() {
            IRQ_CONTROL.IRQsignal(3);
        }

        public void Tick(int c) {

        }
    }
}

