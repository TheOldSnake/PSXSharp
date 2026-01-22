using PSXSharp.Core;
using PSXSharp.Peripherals.GPU;
using PSXSharp.Peripherals.Timers;
using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace PSXSharp {
    public class GPU {
        public Range Range = new Range(0x1f801810, 5);
        public enum GPUState {
            LoadingPrimitive,
            Idle,
            Transferring
        }

        public enum VerticalResolution {
            Y240Lines = 0,
            Y480Lines = 1
        }

        public enum DmaDirection {
            Off = 0,
            Fifo = 1,
            CpuToGp0 = 2,
            VRamToCpu = 3
        }

        public enum TransferType { //My own states, because the CPU fucks up the main DMA direction above in the middle of a transfer
            Off = 0x0,
            VramFill = 0x2,
            VramToVram = 0x4,     
            CpuToVram = 0x5,
            VramToCpu = 0x6,
        }

        enum VideoMode {
            NTSC = 0,
            PAL = 1
        }

        enum DisplayDepth {
            D15Bits = 0,
            D24Bits = 1
        }
        
        public VerticalResolution VerticalRes;
        VideoMode VMode;
        DisplayDepth displayDepth;
        DmaDirection dmaDirection;

        uint GpuInfoSelcted; //For GPUREAD
        uint LastGPURead = 0;

        byte PageBaseX;
        byte PageBaseY;
        byte SemiTransparency;
        byte TextureDepth;
        bool DrawToDisplay;
        bool Dithering;
        public bool ForceSetMaskBit;
        public bool PreserveMaskedPixels;
        byte Field;
        bool TextureDisable;
        bool Interlaced;
        bool DisplayDisabled;
        bool Interrupt;
        int CurrentLine = 0; //Even/Odd flipping

        bool RectangleTextureXFlip;
        bool RectangleTextureYFlip;

        uint TextureWindowXMask;
        uint TextureWindowYMask;
        uint TextureWindowXOffset;
        uint TextureWindowYOffset;
        ushort DrawingAreaLeft;
        ushort DrawingAreaRight;
        ushort DrawingAreaTop;
        ushort DrawingAreaBottom;
        short DrawingXOffset;
        short DrawingYOffset;
        byte HorizontalResolution1; //(0=256, 1=320, 2=512, 3=640)
        byte HorizontalResolution2; //0=256/320/512/640 (from HorizontalResolution1), 1=368)
        public ushort DisplayVramXStart;
        public ushort DisplayVramYStart;
        public ushort DisplayHorizontalStart;
        public ushort DisplayHorizontalEnd;
        public ushort DisplayLineStart;
        public ushort DisplayLineEnd;

        Primitive primitive = null;
    
        public GPU_MemoryTransfer CurrentTransfare;
        public GPUState currentState = GPUState.Idle;

        public float HorizontalRange = 640f;
        public float VerticalRange = 240f;

        //Timing stuff        
        Timer0 TIMER0;
        Timer1 TIMER1;

        double Scanlines = 0;
        double VideoCycles = 0;
        double ScanlinesPerFrame = 263;          //NTSC
        double VideoCyclesPerScanline = 3413;    //NTSC
        int CyclesPerPixel = 4;                  //x=640 (maybe wrong lol)
        double DotClock = 0;

        public GPU(Timer0 timer0, Timer1 timer1) {
            PageBaseX = 0;
            PageBaseY = 0;
            SemiTransparency = 0;
            Dithering = false;
            ForceSetMaskBit = false;
            DrawToDisplay = false;
            PreserveMaskedPixels = false;
            TextureDisable = false;
            HorizontalResolution1 = 3;
            HorizontalResolution2 = 0;
            VerticalRes = VerticalResolution.Y240Lines;
            VMode = VideoMode.NTSC;
            displayDepth = DisplayDepth.D15Bits;
            Interlaced = false;
            DisplayDisabled = true;
            Interrupt = false;
            dmaDirection = DmaDirection.Off;
            TIMER0 = timer0;
            TIMER1 = timer1;
            VblankEventCallback = VblankEvent;
            HblankEventCallback = HblankEvent;
        }

        public uint ReadGPUSTAT() { //TODO make this more accurate depending on GPU state
            uint status = 0;

            status |= ((uint)PageBaseX) << 0;
            status |= ((uint)PageBaseY) << 4;
            status |= ((uint)SemiTransparency) << 5;
            status |= ((uint)TextureDepth) << 7;
            status |= ((uint)(Dithering ? 1 : 0)) << 9;
            status |= ((uint)(DrawToDisplay ? 1 : 0)) << 10;
            status |= ((uint)(ForceSetMaskBit ? 1 : 0)) << 11;
            status |= ((uint)(PreserveMaskedPixels ? 1 : 0)) << 12;
            status |= ((uint)Field) << 13;

            //Bit 14 "Reverseflag" is not supported, or is it? [Insert Vsauce music]

            status |= ((uint)(TextureDisable ? 1 : 0)) << 15;
            status |= ((uint)HorizontalResolution2 & 1) << 16;
            status |= ((uint)HorizontalResolution1 & 3) << 17;

            //Bit 19 locks the bios if we don't emulate bit 31
            status |= ((uint)VerticalRes) << 19; 

            status |= ((uint)VMode) << 20;
            status |= (((uint)displayDepth) << 21);
            status |= ((uint)(Interlaced ? 1 : 0)) << 22;
            status |= ((uint)(DisplayDisabled ? 1 : 0)) << 23;
            status |= ((uint)(Interrupt ? 1 : 0)) << 24;

            //Ready to recive command
            status |= ((uint)(currentState == GPUState.Idle ? 1 : 0)) << 26;   

            //Ready to send VRam to CPU; via GPUREAD
            status |= 1 << 27;

            /*
             Bit28: Normally, this bit gets cleared when the command execution is busy (ie. once when the command and all of its parameters are received), however, for Polygon and Line Rendering commands, the bit gets cleared immediately after receiving the command word (ie. before receiving the vertex parameters).
             */

            //bool notReady = currentState == GPUState.LoadingPrimitive || (gpuTransfer.transferType != TransferType.Off && gpuTransfer.paramsReady);

            /*if (notReady) {
                status &= (~((uint)1 << 28));   //Ready to recive DMA block, this is wrong so I hardcoded 1 
            } else {
                status |= (1 << 28);
            }*/

            status |= (1 << 28);

            status |= ((uint)dmaDirection) << 29;

            status |= ((uint)CurrentLine) << 31;

            uint dma_request;
            switch (dmaDirection) {
                case DmaDirection.Off: dma_request = 0;  break;
                case DmaDirection.Fifo: dma_request = 1;break;      //(if full it should be 0)
                case DmaDirection.CpuToGp0: dma_request = (status >> 28) & 1; break;
                case DmaDirection.VRamToCpu: dma_request = (status >> 27) & 1; break;
                default: throw new Exception("Invalid DMA direction: " + dmaDirection);
            }

            status |= ((uint)dma_request) << 25;

            //return 0b0101_1110_1000_0000_0000_0000_0000_0000;
            return status;
        }

        const int CPUCyclesPerFrame = 564482;   // = ~ 33868899 / 60
        const int CPUCyclesPerHblank = 2146;    // = ~ 564482 / 263

        public Action VblankEventCallback;
        public Action HblankEventCallback;

        public void VblankEvent() {
            if (!DisplayDisabled) {
                EmulatorWindow.UpdateWindow();
            }

            //In 480-lines mode, bit31 changes per frame.
            if (VerticalRes == VerticalResolution.Y480Lines) {
                CurrentLine = (CurrentLine + 1) & 1;
            }

            IRQ_CONTROL.IRQsignal(0);     //VBLANK
            Interrupt = true;
            TIMER1.VblankTick();
            Scheduler.ScheduleEvent(CPUCyclesPerFrame, VblankEventCallback, Event.Vblank);
        }

        public void HblankEvent() {

            //In 240-lines mode, the bit changes per scanline. 
            if (VerticalRes == VerticalResolution.Y240Lines) {
                CurrentLine = (CurrentLine + 1) & 1;
                Scheduler.ScheduleEvent(CPUCyclesPerHblank, HblankEventCallback, Event.Hblank);
            }

            //We should also handle TIMER 0 sync 
        }

        public void WriteGP0(uint value) {

            switch (currentState) {
                case GPUState.Idle: GP0Decode(value); break;

                case GPUState.LoadingPrimitive:
                    primitive.Add(value);
                    if (primitive.IsReady()) {
                        HandlePrimitive();
                        currentState = GPUState.Idle;
                    }
                    break;

                case GPUState.Transferring:
                    CurrentTransfare.Add(value);
                    if (CurrentTransfare.ReadyToExecute) {
                        HandleTransfer();
                    }
                    break;
            }
        }

        private void GP0Decode(uint value) {
            //Depending on the value of these 3 bits, further decoding of the other bits can be done.
            uint opcode = value >> 29;

            switch (opcode) {
                case 0x00: MiscCommands(value); break;

                case 0x01:      //Polygon Commands
                    primitive = new Polygon(value, Dithering, SemiTransparency);
                    currentState = GPUState.LoadingPrimitive;
                    break;

                case 0x02:      //Line Commands
                    primitive = new Line(value, Dithering, SemiTransparency);
                    currentState = GPUState.LoadingPrimitive;
                    break;

                case 0x03:      //Rectangle Commands
                    ushort page = (ushort)(PageBaseX | (((uint)PageBaseY) << 4));
                    primitive = new Rectangle(value, page, SemiTransparency, TextureDepth);
                    currentState = GPUState.LoadingPrimitive;
                    break;

                case 0x04:      //Transfer Commands
                case 0x05:
                case 0x06:
                    currentState = GPUState.Transferring;
                    CurrentTransfare = new GPU_MemoryTransfer(opcode == (uint)TransferType.VramToVram ? 4 : 3, opcode);
                    CurrentTransfare.Add(value);    
                    break;

                                //Environment commands
                case 0x07: EnvironmentCommands(value); break;

                default: throw new Exception("GP0: " + opcode.ToString("x") + " - " + (value >> 29));
            }
        }

        private void HandlePrimitive() {
            if (primitive.GetType() == typeof(Polygon)) {
                //Textured polygons overwrite global settings bits 0-8 
                //We need to read get the "Texpage Attribute" and change the global draw settings to emulate that behavior
                Polygon poly = (Polygon)primitive;
                if (poly.IsTextured()) {
                    uint drawMode = poly.GetDrawMode();
                    this.PageBaseX = (byte)(drawMode & 0xF);
                    this.PageBaseY = (byte)((drawMode >> 4) & 1);
                    this.SemiTransparency = (byte)((drawMode >> 5) & 3);
                    this.TextureDepth = (byte)((drawMode >> 7) & 3);
                }
            }
            primitive.Draw();
        }

        private void HandleTransfer() {
            switch (CurrentTransfare.Type) {
                case (uint)TransferType.VramFill:
                    GLRenderBackend.VramFillRectangle(ref CurrentTransfare);
                    CurrentTransfare = null;
                    currentState = GPUState.Idle;
                    break;

                case (uint)TransferType.VramToVram:
                    GLRenderBackend.VramToVramCopy(ref CurrentTransfare);
                    CurrentTransfare = null;
                    currentState = GPUState.Idle;
                    break;

                case (uint)TransferType.CpuToVram:
                    GLRenderBackend.CpuToVramCopy(ref CurrentTransfare);
                    CurrentTransfare = null;
                    currentState = GPUState.Idle;
                    break;

                case (uint)TransferType.VramToCpu:
                    GLRenderBackend.VramToCpuCopy(ref CurrentTransfare);
                    break;

                default: throw new NotImplementedException();
            }
        }

        private void MiscCommands(uint command) {
            uint opcode = command >> 24;

            switch (opcode) {
                case 0x00: break;      //NOP
                case 0x01: break;      //Clear Cache
                case 0x03: break;      //Unknown?
                case 0xE0: break;      //NOP?
                case uint when opcode >= 0x04 && opcode <= 0x1E: break; //NOP?
                case uint when opcode >= 0xE7 && opcode <= 0xEF: break; //NOP?

                case 0x02:  //Vram Fill
                    currentState = GPUState.Transferring;
                    CurrentTransfare = new GPU_MemoryTransfer(3, 2);
                    CurrentTransfare.Add(command);
                    break;

                default: throw new Exception("Unknown GP0 misc command: " + opcode.ToString("x"));
            }
        }

        private void EnvironmentCommands(uint command) {
            uint opcode = command >> 24 ;
            switch (opcode) {
                case 0xE0: break; //NOP
                case 0xE1: GP0DrawMode(command); break;
                case 0xE2: GP0TextureWindow(command); break;
                case 0xE3: GP0DrawingAreaTopLeft(command); break;
                case 0xE4: GP0DrawingAreaBottomRight(command); break;
                case 0xE5: GP0DrawingOffset(command); break;
                case 0xE6: GP0MaskBit(command); break;
                case uint when opcode >= 0xE7 && opcode <= 0xEF: break; //NOP
                default: throw new Exception("Unknown GP0 Environment command: " + opcode.ToString("x"));
            }
        }

        public void ClearMemory<T>(ref List<T> list) {
            list.Clear();
            list.Capacity = 0;
            list.TrimExcess();
        }
       
        private void GP0MaskBit(uint value) {
            ForceSetMaskBit = ((value & 1) != 0);
            PreserveMaskedPixels = (((value >> 1) & 1) != 0);
            GLRenderBackend.SetMaskBitSetting((int)(value & 0x3));
            //Console.WriteLine($"Mask bit = {value & 3:x}");
        }

        private void GP0TextureWindow(uint value) {
            value &= 0xfffff;   //20 bits

            //in 8 pixel steps, handled in GLSL
            TextureWindowXMask = (value & 0x1f);
            TextureWindowYMask = ((value >> 5) & 0x1f);

            TextureWindowXOffset = ((value >> 10) & 0x1f);
            TextureWindowYOffset = ((value >> 15) & 0x1f);

            GLRenderBackend.SetTextureWindow((ushort)TextureWindowXMask, (ushort)TextureWindowYMask, 
                (ushort)TextureWindowXOffset, (ushort)TextureWindowYOffset);
        }
   
        private void GP0DrawingOffset(uint value) {
            ushort x = (ushort)(value & 0x7ff);
            ushort y = (ushort)((value >> 11) & 0x7ff);

            //Signed 11 bits, need to extend to signed 16 then shift down to 11
            DrawingXOffset = (short)(((short)(x << 5)) >> 5);
            DrawingYOffset = (short)(((short)(y << 5)) >> 5);

            GLRenderBackend.SetOffset(DrawingXOffset, DrawingYOffset);
        }

        private void GP0DrawingAreaBottomRight(uint value) {
            DrawingAreaBottom = (ushort)((value >> 10) & 0x3ff);
            DrawingAreaRight = (ushort)(value & 0x3ff);
            GLRenderBackend.SetScissorBox(DrawingAreaLeft, DrawingAreaTop,
                DrawingAreaRight - DrawingAreaLeft, DrawingAreaBottom - DrawingAreaTop);
        }

        private void GP0DrawingAreaTopLeft(uint value) {
            DrawingAreaTop = (ushort)((value >> 10) & 0x3ff);
            DrawingAreaLeft = (ushort)(value & 0x3ff);
            GLRenderBackend.SetScissorBox(DrawingAreaLeft, DrawingAreaTop,
                DrawingAreaRight - DrawingAreaLeft, DrawingAreaBottom - DrawingAreaTop);
        }

        private void WriteGP1(uint value) {
            uint opcode = (value >> 24) & 0xff;

            switch (opcode) {
                case 0x00: GP1Reset(); break;
                case 0x01: GP1ResetCommandBuffer(); break;
                case 0x02: GP1AcknowledgeIRQ();  break;
                case 0x03: GP1DisplayEnable(value); break;
                case 0x04: GP1DMADirection(value); break;
                case 0x05: GP1DisplayVramStart(value); break;
                case 0x06: GP1DisplayHorizontalRange(value); break;
                case 0x07: GP1DisplayVerticalRange(value); break;
                case 0x08: GP1DisplayMode(value); break;
                case uint when opcode >= 0x10 && opcode <= 0x1F: GpuInfoSelcted = value; break; //Get GPU info

                default: throw new Exception("Unhandled GP1 command :" + value.ToString("X") + " Opcode: " + opcode.ToString("x"));
            }
        }

        private void GP1ResetCommandBuffer() {
            //Reset Fifo, which I don't emulate
            primitive = null;
            currentState = GPUState.Idle;
            CurrentTransfare = null;
        }

        private void GP1AcknowledgeIRQ() {
            Interrupt = false;
        }

        private void GP1DisplayEnable(uint value) {
            DisplayDisabled = (value & 1) != 0;
        }

        private void GP1DisplayVerticalRange(uint value) {
            DisplayLineStart = (ushort)(value & 0x3ff);
            DisplayLineEnd = (ushort)((value>>10) & 0x3ff);
            UpdateVerticalRange();
        }

        private void GP1DisplayHorizontalRange(uint value) {
            DisplayHorizontalStart = (ushort)(value & 0xfff);
            DisplayHorizontalEnd = (ushort)((value>>12) & 0xfff);
            UpdateHorizontalRange();
        }

        private void GP1DisplayVramStart(uint value) {
            DisplayVramXStart = (ushort)(value & 0x3fe);
            DisplayVramYStart = (ushort)((value>>10) & 0x1ff);
        }

        private void GP1DMADirection(uint value) {
            dmaDirection = (DmaDirection)(value & 3);
        }

        private void GP1DisplayMode(uint value) {
            HorizontalResolution1 = (byte)(value & 3);
            HorizontalResolution2 = (byte)((value >> 6) & 1);
            Interlaced = ((value >> 5) & 1) == 1;

            VerticalResolution oldVR = VerticalRes;
            VerticalRes = Interlaced ? (VerticalResolution)((value >> 2) & 1) : VerticalResolution.Y240Lines;

            if (VerticalRes != oldVR) {
                if (VerticalRes == VerticalResolution.Y240Lines && !Scheduler.HasEventOfType(Event.Hblank)) {
                    Scheduler.ScheduleEvent(CPUCyclesPerHblank, HblankEventCallback, Event.Hblank);

                } else if (VerticalRes == VerticalResolution.Y480Lines && Scheduler.HasEventOfType(Event.Hblank)) {
                    Scheduler.FlushEvents(Event.Hblank);
                }
            }
            
            VMode = (VideoMode)((value >> 3) & 1);

            if (VMode == VideoMode.PAL) {
                ScanlinesPerFrame = 314;
                VideoCyclesPerScanline = 3406.1;
            }
            else {
                ScanlinesPerFrame = 263;
                VideoCyclesPerScanline = 3413.6;
            }
           
            if ((value & 0x80) != 0) {
                throw new Exception("Unsupported display mode: " + value.ToString("X"));
            }

            switch (GetHR()) {
                case 320f: CyclesPerPixel = 8; break;
                case 640f: CyclesPerPixel = 4; break;
                case 256f: CyclesPerPixel = 10; break;
                case 512f: CyclesPerPixel = 5; break;
                case 368f: CyclesPerPixel = 7; break;
            }

            UpdateHorizontalRange();
            UpdateVerticalRange();

            uint depth = (uint)((value >> 4) & 1);
            displayDepth = (DisplayDepth)(depth);       //Not needed
            EmulatorWindow.Is24bpp = depth == 1;
        }

        private void GP0DrawMode(uint value) {
            PageBaseX = (byte)(value & 0xf);
            PageBaseY = (byte)((value >> 4) & 1);
            SemiTransparency = (byte)((value >> 5) & 3);
            TextureDepth = (byte)((value >> 7) & 3);
            Dithering = ((value >> 9) & 1) != 0;
            DrawToDisplay = ((value >> 10) & 1) != 0;
            TextureDisable = ((value >> 11) & 1) != 0;                    //Texture page Y Base 2 (N*512) (only for 2MB VRAM)
            RectangleTextureXFlip = ((value >> 12) & 1) != 0;
            RectangleTextureYFlip = ((value >> 13) & 1) != 0;            
        }

        private void GP1Reset() {
            Interrupt = false;
            PageBaseX = 0;
            PageBaseY = 0;
            SemiTransparency = 0;
            TextureWindowXMask = 0;
            TextureWindowYMask = 0;
            TextureWindowXOffset = 0;
            TextureWindowYOffset = 0;
            Dithering = false;
            DrawToDisplay = false;
            TextureDisable = false;
            RectangleTextureXFlip = false;
            RectangleTextureYFlip = false;
            DrawingAreaBottom = 0;
            DrawingAreaTop = 0;
            DrawingAreaLeft = 0;
            DrawingAreaRight = 0;
            DrawingXOffset = 0;
            DrawingYOffset = 0;
            ForceSetMaskBit = false;
            PreserveMaskedPixels = false;
            dmaDirection = DmaDirection.Off;
            DisplayDisabled = true;
            DisplayVramXStart = 0;
            DisplayVramYStart = 0;
            HorizontalResolution1 = 3;
            HorizontalResolution2 = 0;
            VerticalRes = VerticalResolution.Y240Lines;
            VMode = VideoMode.NTSC;
            Interlaced = true;

            DisplayHorizontalStart = 0x200;
            DisplayHorizontalEnd = 0xc00;

            DisplayLineStart = 0x10;
            DisplayLineEnd = 0x100;

            displayDepth = DisplayDepth.D15Bits;

            currentState = GPUState.Idle;
            CurrentLine = 0;

            //Since we reset to Vertical Resolution = 240 we need to schedule hblank
            if (!Scheduler.HasEventOfType(Event.Hblank)) {
                Scheduler.ScheduleEvent(CPUCyclesPerHblank, HblankEventCallback, Event.Hblank);
            }

            GLRenderBackend.DisableBlending();
            //Probably more window reset stuff
        }

        public uint GPUReadRegister() {
            //Handle responding to GP0(C0h) (Vram to CPU), if it exists 
            if (CurrentTransfare != null && CurrentTransfare.Type == (uint)TransferType.VramToCpu) {
                uint data = CurrentTransfare.ReadWord();
                if (CurrentTransfare.DataEnd) {
                    CurrentTransfare = null;
                    currentState = GPUState.Idle;
                }
                LastGPURead = data;
                return data;
            }

            int start = 0;
            int end = 0x8;  //Excluded
            uint final = (uint)(start + ((GpuInfoSelcted - start) % (end - start)));

            switch (final) {
                case 0x0:
                case 0x1:
                case 0x6:
                case 0x7:
                    return LastGPURead;

                case 0x2: 
                    LastGPURead = TextureWindowXMask | (TextureWindowYMask << 5) | 
                        (TextureWindowXOffset << 10) | (TextureWindowYOffset << 15);
                    break;
                
                case 0x3: LastGPURead = (uint)(DrawingAreaLeft | DrawingAreaTop << 10); break;
                case 0x4: LastGPURead = (uint)(DrawingAreaRight | DrawingAreaBottom << 10); break;
                case 0x5:
                    uint y = (uint)DrawingYOffset;
                    uint x = (uint)DrawingXOffset;
                    LastGPURead = (uint)(x | (y << 11));
                    break;

                default:
                    throw new Exception("Unknown GPU read info selected: ");

            }
            return LastGPURead;

        }

        public void UpdateHorizontalRange() {
            HorizontalRange = (((DisplayHorizontalEnd - DisplayHorizontalStart) / CyclesPerPixel) + 2) & (~3);
        }

        public void UpdateVerticalRange() {
            float verticalRange = DisplayLineEnd - DisplayLineStart;
            VerticalRange = Interlaced? verticalRange * 2 : verticalRange;
        }

        public float GetHR() {
            if (HorizontalResolution2 == 1) {
                return 368f;
            } else {
                switch (HorizontalResolution1) {
                    case 0: return 256.0f;
                    case 1: return 320.0f;
                    case 2: return 512.0f;
                    case 3: return 640.0f;
                }
            }
            return 0f;
        }

        public uint LoadWord(uint address) {
            uint offset = address - Range.Start;
            switch (offset) {
                case 0: return GPUReadRegister();
                case 4: return ReadGPUSTAT();
                default: throw new Exception("Unhandled read to offset " + offset);
            }
        }

        public void WriteWord(uint address, uint value) {
            uint offset = address - Range.Start;
            switch (offset) {
                case 0: WriteGP0(value); break;
                case 4: WriteGP1(value); break;
                default:
                    throw new Exception("Unhandled write to offset: " + offset + " val: " + value.ToString("x")
                                        + "Physical address: " + offset.ToString("x"));
            }
        }
    }
}
