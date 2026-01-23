using System;
using PSXSharp.Core;
using PSXSharp.Peripherals.IO;
using PSXSharp.Peripherals.MDEC;
using PSXSharp.Peripherals.Timers;
using static PSXSharp.DMAChannel;

namespace PSXSharp {
    //The component will be created outside the BUS, and passed through the constructor
    public unsafe partial class BUS {
        public BIOS               BIOS               { get; set; }
        public MemoryControl      MemoryControl      { get; set; }
        public RAM_SIZE           RamSize            { get; set; }
        public CACHECONTROL       CacheControl       { get; set; } 
        public RAM                RAM                { get; set; } 
        public SPU                SPU                { get; set; }
        public Expansion1         Expansion1         { get; set; }
        public Expansion2         Expansion2         { get; set; }
        public DMA                DMA                { get; set; }
        public GPU                GPU                { get; set; }
        public CD_ROM             CDROM              { get; set; }
        public Timer0             Timer0             { get; set; }
        public Timer1             Timer1             { get; set; }
        public Timer2             Timer2             { get; set; }
        public JOY                JOY_IO             { get; set; }
        public SIO1               SerialIO1          { get; set; }
        public Scratchpad         Scratchpad         { get; set; }
        public MacroblockDecoder  MDEC               { get; set; }

        public Action<DMAChannel> DMAHandler => HandleDMA;
        private readonly Action<uint, uint, uint, DirectionType>[] DMAChannelHandlers;
        private Action Callback;

        //Paging constants for a 64-KB pages
        private const int PAGE_SHIFT = 16;                     //16-bits for 64-KB
        private const int PAGE_COUNT = 1 << (32 - PAGE_SHIFT); //Total number of pages in a 32-bit address space
        private const int PAGE_SIZE = 1 << PAGE_SHIFT;         //Size of each page in bytes
        private const int PAGE_OFFSET_MASK = PAGE_SIZE - 1;    //Mask to extract offset within a page

        //Size (in bytes) of a full page table where each entry is a pointer-sized value
        private readonly uint PageTableSizeBytes = (uint)(PAGE_SIZE * nuint.Size);

        //Page tables for fast RAM/BIOS access (pointers to pointers, certified C moment).
        private readonly byte** ReadPageTable;
        private readonly byte** WritePageTable;

        //IO Read/Write Mapping
        private readonly IO32[] IO32Map;
        private readonly IO16[] IO16Map;
        private readonly IO8[] IO8Map;

        //Scratchpad is only accessable via KUSEG and KSEG0
        private bool IsScratchPad(uint page, uint offset) => (page == 0x1F80 || page == 0x9F80) && offset < 0x400;

        //Helper to mask region bits
        //Masks the region bits using bits [29:31] as index into the table
        public static uint ToPhysical(uint address) => address & RegionMaskTable[address >> 29];

        private static readonly uint[] RegionMaskTable = [
            //KUSEG: 2048MB
            0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF,
            //KSEG0: 512MB
            0x7FFFFFFF,
            //KSEG1: 512MB
            0x1FFFFFFF,
            //KSEG2: 1024MB
            0xFFFFFFFF, 0xFFFFFFFF
        ];

        //Locked IO locations, there are more in PSX-SPX
        private static readonly (uint start, uint end)[] LockedRanges = [
                (0x1F800400, 0x1F800400 + 0xC00 - 1),
                (0x1F801024, 0x1F801024 + 0x1C  - 1),
                (0x1F801064, 0x1F801064 + 0x0C  - 1),
                (0x1F801078, 0x1F801078 + 0x08  - 1),
                (0x1F801140, 0x1F801140 + 0x6C0 - 1),
                (0x1F801804, 0x1F801804 + 0x0C  - 1),
                (0x1F801818, 0x1F801818 + 0x08  - 1),
                (0x1F801828, 0x1F801828 + 0x3D8 - 1),
        ];

        public bool Debug; //To be removed
        public BUS(
            BIOS bios,
            RAM ram,
            Scratchpad scratchpad,
            CD_ROM cdrom,
            SPU spu,
            JOY joyIO,
            SIO1 serialIO1,
            MemoryControl memoryControl,
            RAM_SIZE ramSize,
            CACHECONTROL cacheControl,
            Expansion1 expansion1,
            Expansion2 expansion2,
            Timer0 timer0,
            Timer1 timer1,
            Timer2 timer2,
            MacroblockDecoder mdec,
            GPU gpu,
            DMA dma){

            //Assign all components
            BIOS = bios;
            RAM = ram;
            Scratchpad = scratchpad;
            CDROM = cdrom;
            SPU = spu;
            JOY_IO = joyIO;
            SerialIO1 = serialIO1;
            MemoryControl = memoryControl;
            RamSize = ramSize;
            CacheControl = cacheControl;
            Expansion1 = expansion1;
            Expansion2 = expansion2;
            Timer0 = timer0;
            Timer1 = timer1;
            Timer2 = timer2;
            MDEC = mdec;
            GPU = gpu;
            DMA = dma;

            //Set up the DMA
            DMA.SetHandler(DMAHandler);
            Callback = DMAIRQ;
            DMAChannelHandlers = [
                HandleMDECIn,       //Channel 0
                HandleMDECOut,      //Channel 1
                HandleGPUDMA,       //Channel 2
                HandleCDROMDMA,     //Channel 3
                HandleSPUDMA,       //Channel 4
                HandlePIODMA,       //Channel 5
                HandleOTCDMA        //Channel 6
            ];

            //Set up fastmem
            ReadPageTable = (byte**)NativeMemoryManager.AllocateNativeMemory(PageTableSizeBytes);
            WritePageTable = (byte**)NativeMemoryManager.AllocateNativeMemory(PageTableSizeBytes);
            InitlizeFastmem();

            //Create IO maps
            IO32Map = CreateIO32Map();
            IO16Map = CreateIO16Map();
            IO8Map  = CreateIO8Map();
        }

        private void InitlizeFastmem() {
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
            //we map it 4 times to the same host pages
            for (uint i = 0; i < 4; i++) {
                uint mirrorOffset = i * RAM.SIZE;
                MapPages(RAM.BASE_ADDRESS + mirrorOffset, RAM.SIZE, RAM.NativeAddress, ReadPageTable);
                MapPages(RAM.BASE_ADDRESS + mirrorOffset, RAM.SIZE, RAM.NativeAddress, WritePageTable);
            }
        }

        private void MapPages(uint baseGuestAddress, uint length, byte* baseNativeAddress, byte** pageTable) {
            uint maskesAddress = ToPhysical(baseGuestAddress);
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

        private uint ReadWordIO(uint virtualAddress) {
            uint physicalAddress = ToPhysical(virtualAddress);
            ReadOnlySpan<IO32> IO32Span = IO32Map;

            for (int i = 0; i < IO32Span.Length; i++) {
                var IO32 = IO32Span[i];
                if (IO32.Range.Contains(physicalAddress) && IO32.Read != null) {
                    return IO32.Read(physicalAddress);
                }
            }

            if (IsLockedLocations(physicalAddress)) {
                return 0xFFFFFFFF;
            } else {
                throw new Exception($"Unhandled LoadWord from: {physicalAddress:X8}");
            }
        }

        private ushort ReadHalfIO(uint virtualAddress) {
            uint physicalAddress = ToPhysical(virtualAddress);
            ReadOnlySpan<IO16> IO16Span = IO16Map;

            for (int i = 0; i < IO16Span.Length; i++) {
                var IO16 = IO16Span[i];
                if (IO16.Range.Contains(physicalAddress) && IO16.Read != null) {
                    return IO16.Read(physicalAddress);
                }
            }

            if (IsLockedLocations(physicalAddress)) {
                return 0xFFFF;
            } else {
                throw new Exception($"Unhandled LoadHalf from: {physicalAddress:X8}");
            }
        }

        private byte ReadByteIO(uint virtualAddress) {
            uint physicalAddress = ToPhysical(virtualAddress);
            ReadOnlySpan<IO8> IO8Span = IO8Map;

            for (int i = 0; i < IO8Span.Length; i++) {
                var IO8 = IO8Span[i];
                if (IO8.Range.Contains(physicalAddress) && IO8.Read != null) {
                    return IO8.Read(physicalAddress);
                }
            }

            if (IsLockedLocations(physicalAddress)) {
                return 0xFF;
            } else {
                throw new Exception($"Unhandled LoadByte from: {physicalAddress:X8}");
            }
        }

        private void WriteWordIO(uint address,uint value) {
            uint physicalAddress = ToPhysical(address);
            ReadOnlySpan<IO32> IO32Span = IO32Map;

            for (int i = 0; i < IO32Span.Length; i++) {
                var IO32 = IO32Span[i];
                if (IO32.Range.Contains(physicalAddress) && IO32.Write != null) {
                    IO32.Write(physicalAddress, value);
                    return;
                }
            }

            if (IsLockedLocations(physicalAddress)) {
                return;
            } else {
                throw new Exception($"Unhandled StoreWord to: {physicalAddress:X8}");
            }
        }

        private void WriteHalfIO(uint address, ushort value) {
            uint physicalAddress = ToPhysical(address);
            ReadOnlySpan<IO16> IO16Span = IO16Map;

            for (int i = 0; i < IO16Span.Length; i++) {
                var IO16 = IO16Span[i];
                if (IO16.Range.Contains(physicalAddress) && IO16.Write != null) {
                    IO16.Write(physicalAddress, value);
                    return;
                }
            }

            if (IsLockedLocations(physicalAddress)) {
                return;
            } else {
                throw new Exception($"Unhandled StoreHalf to: {physicalAddress:X8}");
            }
        }

        private void WriteByteIO(uint address, byte value) {
            uint physicalAddress = ToPhysical(address);
            ReadOnlySpan<IO8> IO8Span = IO8Map;

            for (int i = 0; i < IO8Span.Length; i++) {
                var IO8 = IO8Span[i];
                if (IO8.Range.Contains(physicalAddress) && IO8.Write != null) {
                    IO8.Write(physicalAddress, value);
                    return;
                }
            }

            if (IsLockedLocations(physicalAddress)) {
                return;
            } else {
                throw new Exception($"Unhandled StoreByte to: {physicalAddress:X8}");
            }
        }

        private bool IsLockedLocations(uint physicalAddress) {
            //Check if the address falls within any range
            foreach (var range in LockedRanges) {
                if (physicalAddress >= range.start && physicalAddress <= range.end) {
                    return true;
                }
            }

            return false;
        }
    }
}

