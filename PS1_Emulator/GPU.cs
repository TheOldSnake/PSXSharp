﻿using OpenTK.Graphics.ES20;
using PS1_Emulator.PS1_Emulator;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Text;
using System.Threading.Tasks;
using static PS1_Emulator.GPU;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace PS1_Emulator {
    public class GPU {
        public Range range = new Range(0x1f801810, 5);            //Assumption  
        Renderer window;

        public enum VerticalRes {
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
            Off = 0,
            CpuToGp0 = 0xA0,
            VRamToCpu = 0xC0,
            VramToVram = 0x80,
            VramFill = 0x02        //Does it really belong here? ask nocash
        }
        enum VMode {
            NTSC = 0,
            PAL = 1
        }
        enum DisplayDepth {
            D15Bits = 0,
            D24Bits = 1
        }
        
        public VerticalRes verticalRes;
        VMode vmode;
        DisplayDepth displayDepth;
        DmaDirection dmaDirection;

        uint gpuInfoSelcted; //For GPUREAD

        byte page_base_x;
        byte page_base_y;
        byte semi_transparency;
        byte texture_depth;
        bool draw_to_display;
        bool dithering;
        bool force_set_mask_bit;
        bool preserve_masked_pixels;
        byte field;
        bool texture_disable;
        public HorizontalRes horizontalRes;
        bool interlaced;
        bool display_disabled;
        bool interrupt;

        bool rectangle_texture_x_flip;
        bool rectangle_texture_y_flip;

        UInt32 texture_window_x_mask;
        UInt32 texture_window_y_mask;
        UInt32 texture_window_x_offset;
        UInt32 texture_window_y_offset;
        UInt16 drawing_area_left;
        UInt16 drawing_area_right;
        UInt16 drawing_area_top;
        UInt16 drawing_area_bottom;
        Int16 drawing_x_offset;
        Int16 drawing_y_offset;
        UInt16 display_vram_x_start;
        UInt16 display_vram_y_start;
        public UInt16 display_horiz_start;
        public UInt16 display_horiz_end;
        public UInt16 display_line_start;
        public UInt16 display_line_end;
        Primitive primitive = null;
        enum GPUState {
            LoadingPrimitive,
            ReadyToDecode,
            Transferring
        }
        public struct GPUTransfer {
            public uint[] parameters;
            public ushort[] data;
            public int dataPtr;
            public int paramPtr;
            public bool paramsReady => paramPtr == parameters.Length;
            private uint resolution => parameters[2];


            //Xsiz=((Xsiz AND 3FFh)+0Fh) AND (NOT 0Fh) for vram fill?
            public ushort width => (ushort)(transferType == TransferType.VramFill?
                (resolution & 0x3FF) : ((((resolution & 0xFFFF) - 1) & 0x3FF) + 1));

            public ushort height => (ushort)(transferType == TransferType.VramFill?
              ((resolution >> 16) & 0x1FF) : ((((resolution >> 16) - 1) & 0x1FF) + 1));

            public ushort size => (ushort)(((width * height) + 1) & ~1);
            public bool dataEnd => size == dataPtr;
            public ushort destination_X => (ushort)(parameters[1] & (transferType == TransferType.VramFill ? 0x3F0 : 0x3FF));
            public ushort destination_Y => (ushort)((parameters[1] >> 16) & 0x1FF);
            public float fillColor_R => (float)((parameters[0] & 0xFF) / 255.0);
            public float fillColor_G => (float)(((parameters[0] >> 8) & 0xFF) / 255.0);
            public float fillColor_B => (float)(((parameters[0] >> 16) & 0xFF) / 255.0);

            public TransferType transferType;        //CPU seems to set the main direction to off in the middle
                                                   //of the transfer, this should keep the direction saved
        }
        public GPUTransfer gpuTransfer;
        GPUState currentState = GPUState.ReadyToDecode;

        TIMER1 TIMER1;

        public GPU(Renderer rederingWindow, ref TIMER1 timer1) {
            this.page_base_x = 0;
            this.page_base_y = 0;
            this.semi_transparency = 0;
            this.dithering = false;
            this.force_set_mask_bit = false;
            this.draw_to_display = false;
            this.preserve_masked_pixels = false;
            this.texture_disable = false;
            this.horizontalRes = new HorizontalRes(0, 0);
            this.verticalRes = VerticalRes.Y240Lines;
            this.vmode = VMode.NTSC;
            this.displayDepth = DisplayDepth.D15Bits;
            this.interlaced = false;
            this.display_disabled = true;
            this.interrupt = false;
            this.dmaDirection = DmaDirection.Off;
            this.window = rederingWindow;
            this.TIMER1 = timer1;

        }


        public UInt32 read_GPUSTAT() { //TODO make this more accurite depending on GPU state
            UInt32 value = 0;

            value |= (((UInt32)this.page_base_x) << 0);
            value |= (((UInt32)this.page_base_y) << 4);
            value |= (((UInt32)this.semi_transparency) << 5);
            value |= (((UInt32)this.texture_depth) << 7);
            value |= ((Convert.ToUInt32(this.dithering)) << 9);
            value |= ((Convert.ToUInt32(this.draw_to_display)) << 10);
            value |= ((Convert.ToUInt32(this.force_set_mask_bit)) << 11);
            value |= ((Convert.ToUInt32(this.preserve_masked_pixels)) << 12);
            value |= (((UInt32)this.field) << 13);

            //Bit 14 is not supported 

            value |= ((Convert.ToUInt32(this.texture_disable)) << 15);
            value |= this.horizontalRes.intoStatus();

            //Bit 19 locks the bios currently
            //value |= (((UInt32)this.vrez) << 19); 

            value |= (((UInt32)this.vmode) << 20);
            value |= (((UInt32)this.displayDepth) << 21);
            value |= ((Convert.ToUInt32(this.interlaced)) << 22);
            value |= ((Convert.ToUInt32(this.display_disabled)) << 23);
            value |= ((Convert.ToUInt32(this.interrupt)) << 24);

            value |= 1 << 26;   //Ready to recive command
            value |= 1 << 27;   //Ready to send VRam to CPU; via GPUREAD
            value |= 1 << 28;   //Ready to recive DMA block

            value |= (((UInt32)this.dmaDirection) << 29);

            value |= 0 << 31;   //Depends on the current line...


            UInt32 dma_request;
            switch (this.dmaDirection) {
                case DmaDirection.Off: 
                    dma_request = 0;
                    break;

                case DmaDirection.Fifo: //(if full it should be 0)
                    dma_request = 1;
                    break;
                case DmaDirection.CpuToGp0:
                    dma_request = (value >> 28) & 1;
                    break;

                case DmaDirection.VRamToCpu: 
                    dma_request = (value >> 27) & 1;
                    break;

                default:
                    throw new Exception("Invalid DMA direction: " + this.dmaDirection);
            }

            value |= ((UInt32)dma_request) << 25;


            //return 0b01011110100000000000000000000000;
            return value;

        }

        double scanlines = 0;
        double scanlines_per_frame = 0;
        double video_cycles = 0;
        double video_cycles_per_scanline = 0;

        public void tick(double cycles) {
            video_cycles += cycles;
            TIMER1.GPUinVblank = false;

            if (video_cycles >= video_cycles_per_scanline && video_cycles_per_scanline > 0) {
                video_cycles -= video_cycles_per_scanline;
                scanlines++;

                if (scanlines >= scanlines_per_frame && scanlines_per_frame > 0) {
                    scanlines -= scanlines_per_frame;

                    if (!display_disabled) {
                        window.display();
                    }

                    IRQ_CONTROL.IRQsignal(0);     //VBLANK
                    interrupt = true;
                    TIMER1.GPUinVblank = true;
                    TIMER1.GPUGotVblankOnce = true;

                }

                if (!TIMER1.isUsingSystemClock()) {
                    TIMER1.tick(1);
                }

            }

        }       
        public void write_GP0(UInt32 value) {
            switch (currentState) {
                case GPUState.ReadyToDecode: 
                    gp0_decode(value);
                    break;

                case GPUState.LoadingPrimitive:
                    primitive.add(value);
                    if (primitive.isReady()) {
                        primitive.draw(ref window);
                        currentState = GPUState.ReadyToDecode;
                    }
                    break;

                case GPUState.Transferring:
                    if (gpuTransfer.paramsReady) {
                        handleTransfer(value);
                    }
                    else {
                        gpuTransfer.parameters[gpuTransfer.paramPtr++] = value;
                        if (gpuTransfer.paramsReady) {
                            gpuTransfer.data = new ushort[gpuTransfer.size];
                            if (gpuTransfer.transferType != TransferType.CpuToGp0) {
                                handleTransfer(value);
                            }
                        }
                    }
                    break;
            }
        }

        private void gp0_decode(uint value) {
            UInt32 opcode = value >> 24;
            switch (opcode) {
                case 0x00: //NOP
                case 0xE0: //NOP
                case uint when opcode >= 0x04 && opcode <= 0x1E: //NOP
                case uint when opcode >= 0xE7 && opcode <= 0xEF: //NOP
                case 0x1: break;  //Clear cache

                //Polygon Commands
                case uint when opcode >= 0x20 && opcode <= 0x3E:
                    primitive = new Polygon(value, semi_transparency);
                    currentState = GPUState.LoadingPrimitive;
                    break;

                //Rectangle Commands
                case uint when opcode >= 0x60 && opcode <= 0x7F:
                    ushort page = (ushort)(page_base_x | (((uint)page_base_y) << 4) | (((uint)texture_depth) << 7));

                    primitive = new Rectangle(value, page, semi_transparency);
                    currentState = GPUState.LoadingPrimitive;
                    break;
                
                //Line Commands
                case uint when opcode >> 5 == 2:
                    primitive = new Line(value, semi_transparency);
                    currentState = GPUState.LoadingPrimitive;
                    break;

                //Environment commands
                case 0xE1: gp0_draw_mode(value); break;
                case 0xE2: gp0_texture_window(value); break;
                case 0xE3: gp0_drawing_area_TopLeft(value); break;
                case 0xE4: gp0_drawing_area_BottomRight(value); break;
                case 0xE5: gp0_drawing_offset(value); break;
                case 0xE6: gp0_mask_bit(value); break;

                //Transfer Commands
                case 0x02:  //Vram Fill
                case 0xA0:  //CPU to GP0
                case 0xC0:  //Vram to CPU
                case 0x80:  //Vram to Vram
                    currentState = GPUState.Transferring;
                    gpuTransfer.transferType = (TransferType)opcode;
                    gpuTransfer.paramPtr = 0;
                    gpuTransfer.dataPtr = 0;
                    gpuTransfer.parameters = new uint[gpuTransfer.transferType == TransferType.VramToVram ? 4 : 3];
                    gpuTransfer.parameters[gpuTransfer.paramPtr++] = value;
                    break;

                default:
                    throw new Exception(opcode.ToString("x") + " - " + (value >> 29));
            }
        }

        private void handleTransfer(uint value) {
            switch (gpuTransfer.transferType) {
                case TransferType.CpuToGp0:
                    gpuTransfer.data[gpuTransfer.dataPtr++] = (ushort)(value & 0xFFFF);
                    gpuTransfer.data[gpuTransfer.dataPtr++] = (ushort)((value >> 16) & 0xFFFF);

                    if (gpuTransfer.dataEnd) {
                        window.update_vram(gpuTransfer.destination_X, gpuTransfer.destination_Y,
                            gpuTransfer.width, gpuTransfer.height, ref gpuTransfer.data);
                        currentState = GPUState.ReadyToDecode;
                        gpuTransfer.transferType = TransferType.Off;
                    }
                    break;

                case TransferType.VRamToCpu:
                    window.readBackTexture(gpuTransfer.destination_X, gpuTransfer.destination_Y,
                        gpuTransfer.width, gpuTransfer.height, ref gpuTransfer.data);
                    //gpuTransfer.transferType = TransferType.Off;
                    currentState = GPUState.ReadyToDecode;
                    break;

                case TransferType.VramToVram:
                    //Console.WriteLine("Vram to Vram"); 
                    /*window.VramToVramCopy((int)(gpuTransfer.parameters[1] & 0xFFFF), (int)((gpuTransfer.parameters[1] >> 16) & 0xFFFF),
                    (int)(gpuTransfer.parameters[2] & 0xFFFF), (int)((gpuTransfer.parameters[2] >> 16) & 0xFFFF), (int)(gpuTransfer.parameters[3] & 0xFFFF), (int)((gpuTransfer.parameters[3] >> 16) & 0xFFFF)
                    );*/
                    gpuTransfer.transferType = TransferType.Off;
                    currentState = GPUState.ReadyToDecode;
                    break;

                case TransferType.VramFill:
                    window.disableBlending();
                    window.vramFill(gpuTransfer.fillColor_R, gpuTransfer.fillColor_G, gpuTransfer.fillColor_B,
                        gpuTransfer.destination_X, gpuTransfer.destination_Y, gpuTransfer.width, gpuTransfer.height);
                    gpuTransfer.transferType = TransferType.Off;
                    currentState = GPUState.ReadyToDecode;

                    break;

                default:
                    Console.WriteLine(value.ToString("x"));

                    Console.WriteLine(gpuTransfer.transferType);
                    Console.WriteLine(gpuTransfer.paramsReady);
                    Console.WriteLine(gpuTransfer.dataEnd);


                    throw new NotImplementedException();

            }
        }

        public void ClearMemory<T>(ref List<T> list) {

            list.Clear();
            list.Capacity = 0;
            list.TrimExcess();
            
        }
       
        private void gp0_mask_bit(UInt32 value) {
            this.force_set_mask_bit = ((value & 1) != 0);
            this.preserve_masked_pixels = (((value >> 1) & 1) != 0);
            window.maskBitSetting((int)value);
        }

        private void gp0_texture_window(UInt32 value) {
            value &= 0xfffff;   //20 bits

            //in 8 pixel steps, handled in GLSL
            this.texture_window_x_mask = (value & 0x1f);
            this.texture_window_y_mask = ((value >> 5) & 0x1f);

            this.texture_window_x_offset = ((value >> 10) & 0x1f);
            this.texture_window_y_offset = ((value >> 15) & 0x1f);

            window.setTextureWindow((ushort)texture_window_x_mask, (ushort)texture_window_y_mask, 
                (ushort)texture_window_x_offset, (ushort)texture_window_y_offset);
        }
   
        private void gp0_drawing_offset(UInt32 value) {
            UInt16 x = (UInt16)(value & 0x7ff);
            UInt16 y = (UInt16)((value>>11) & 0x7ff);

            //Signed 11 bits, need to extend to signed 16 then shift down to 11
            this.drawing_x_offset = (Int16)(((Int16)(x << 5)) >> 5);
            this.drawing_y_offset = (Int16)(((Int16)(y << 5)) >> 5);

            window.setOffset(drawing_x_offset, drawing_y_offset,0);
           
        }

        private void gp0_drawing_area_BottomRight(UInt32 value) {
            this.drawing_area_bottom = (UInt16)((value >> 10) & 0x3ff);
            this.drawing_area_right = (UInt16)(value & 0x3ff);
            window.setScissorBox(drawing_area_left, drawing_area_top,
                drawing_area_right-drawing_area_left, drawing_area_bottom-drawing_area_top);
        }

        private void gp0_drawing_area_TopLeft(UInt32 value) {
            this.drawing_area_top = (UInt16)((value >> 10) & 0x3ff);
            this.drawing_area_left = (UInt16)(value & 0x3ff);
        }

        public void write_GP1(UInt32 value) {
            UInt32 opcode = (value >> 24) & 0xff;

            switch (opcode) {
                case 0x00: gp1_reset(); break;
                case 0x01: gp1_reset_command_buffer(); break;
                case 0x02: gp1_acknowledge_irq();  break;
                case 0x03: gp1_display_enable(value); break;
                case 0x04: gp1_dma_direction(value); break;
                case 0x05: gp1_display_VRam_start(value); break;
                case 0x06: gp1_display_horizontal_range(value); break;
                case 0x07: gp1_display_vertical_range(value); break;
                case 0x08: gp1_display_mode(value); break;
                case uint when opcode >= 0x10 && opcode <= 0x1F: gpuInfoSelcted = value; break; //Get GPU info

                default: throw new Exception("Unhandled GP1 command :" + value.ToString("X") + " Opcode: " + opcode.ToString("x"));
            }

        }

        private void gp1_reset_command_buffer() {
            //Reset Fifo, which I don't emulate
            primitive = null;
            currentState = GPUState.ReadyToDecode;
            gpuTransfer.paramPtr = 0;
            gpuTransfer.dataPtr = 0;
            gpuTransfer.transferType = TransferType.Off;
        }

        private void gp1_acknowledge_irq() {
            this.interrupt = false;
        }

        private void gp1_display_enable(UInt32 value) {
            this.display_disabled = (value & 1) != 0;
        }

        private void gp1_display_vertical_range(UInt32 value) {
            this.display_line_start = (UInt16)(value & 0x3ff);
            this.display_line_end = (UInt16)((value>>10) & 0x3ff);
        }

        private void gp1_display_horizontal_range(UInt32 value) {
            this.display_horiz_start = (UInt16)(value & 0xfff);
            this.display_horiz_end = (UInt16)((value>>12) & 0xfff);
        }

        private void gp1_display_VRam_start(UInt32 value) {
            this.display_vram_x_start = (UInt16)(value & 0x3fe);
            this.display_vram_y_start = (UInt16)((value>>10) & 0x1ff);
        }

        private void gp1_dma_direction(UInt32 value) {
            dmaDirection = (DmaDirection)(value & 3);
        }

        private void gp1_display_mode(UInt32 value) {
            byte hr1 = ((byte)(value & 3));
            byte hr2 = ((byte)((value >> 6) & 1));

            horizontalRes = new HorizontalRes(hr1, hr2);
            verticalRes = (VerticalRes)((value >> 2) & 1);
            vmode = (VMode)((value >> 3) & 1);
            displayDepth = (DisplayDepth)((value >> 4) & 1);

            if (vmode == VMode.PAL) {
                scanlines_per_frame = 314;
                video_cycles_per_scanline = 3406.1;
            }
            else {
                scanlines_per_frame = 263;
                video_cycles_per_scanline = 3413.6;
            }
           
            this.interlaced = (value & 0x20) != 0;

            if ((value & 0x80) !=0) {
                throw new Exception("Unsupported display mode: " + value.ToString("X"));
            }

        }

        private void gp0_draw_mode(UInt32 value) {

            this.page_base_x = (byte)(value & 0xf);
            this.page_base_y = (byte)((value >> 4) & 1);
            this.semi_transparency = (byte)((value >> 5) & 3);
            this.texture_depth = (byte)((value >> 7) & 3);

            this.dithering = (((value >> 9) & 1) != 0);
            this.draw_to_display = (((value >> 10) & 1) != 0);
            this.texture_disable = (((value >> 11) & 1) != 0);
            this.rectangle_texture_x_flip = (((value >> 12) & 1) != 0);
            this.rectangle_texture_y_flip = (((value >> 13) & 1) != 0);

        }
        private void gp1_reset() {
            this.interrupt = false;
            this.page_base_x = 0;
            this.page_base_y = 0;
            this.semi_transparency = 0;
            this.texture_window_x_mask = 0;
            this.texture_window_y_mask = 0;
            this.texture_window_x_offset = 0;
            this.texture_window_y_offset = 0;
            this.dithering = false;
            this.draw_to_display = false;
            this.texture_disable = false;
            this.rectangle_texture_x_flip = false;
            this.rectangle_texture_y_flip = false;
            this.drawing_area_bottom = 0;
            this.drawing_area_top = 0;
            this.drawing_area_left = 0;
            this.drawing_area_right = 0;
            this.drawing_x_offset = 0;
            this.drawing_y_offset = 0;
            this.force_set_mask_bit = false;
            this.preserve_masked_pixels = false;
            this.dmaDirection = DmaDirection.Off;
            this.display_disabled = true;
            this.display_vram_x_start = 0;
            this.display_vram_y_start = 0;
            this.horizontalRes = new HorizontalRes(0, 0);
            this.verticalRes = VerticalRes.Y240Lines;
            this.vmode = VMode.NTSC;
            this.interlaced = true;

            this.display_horiz_start = 0x200;
            this.display_horiz_end = 0xc00;

            this.display_line_start = 0x10;
            this.display_line_end = 0x100;

            this.displayDepth = DisplayDepth.D15Bits;

            this.currentState = GPUState.ReadyToDecode;
            this.gpuTransfer.dataPtr = 0;
            this.gpuTransfer.paramPtr = 0;
            
            //...clear Fifo

        }

        uint lastGPURead = 0x0;
        internal uint gpuReadReg() {
            //Handle responding to GP0(C0h) (Vram to CPU), if it exists 
            if (gpuTransfer.transferType == TransferType.VRamToCpu) {
                ushort pixel0 = gpuTransfer.data[gpuTransfer.dataPtr++];
                ushort pixel1 = gpuTransfer.data[gpuTransfer.dataPtr++];
                uint merged_Pixels = (uint)(pixel0 | (pixel1 << 16));
                if (gpuTransfer.dataEnd) {
                    gpuTransfer.transferType = TransferType.Off;
                }
                return merged_Pixels;
            }

            int start = 0;
            int end = 0x8;  //Excluded
            uint final = (uint)(start + ((gpuInfoSelcted - start) % (end - start)));

            switch (final) {
                case 0x0:
                case 0x1:
                case 0x6:
                case 0x7:
                    return lastGPURead;

                case 0x2: 
                    lastGPURead = (texture_window_x_mask | (texture_window_y_mask << 5) | 
                        (texture_window_x_offset << 10) | (texture_window_y_offset << 15));
                    break;
                
                case 0x3: lastGPURead = (uint)(drawing_area_left | drawing_area_top << 10); break;
                case 0x4: lastGPURead = (uint)(drawing_area_right | drawing_area_bottom << 10); break;
                case 0x5:
                    uint y = (uint)drawing_y_offset;
                    uint x = (uint)drawing_x_offset;
                    lastGPURead = (uint)(x | (y << 11));
                    break;

                default:
                    throw new Exception("Unknown GPU read info selected: ");

            }
            return lastGPURead;

        }
    }

    public class HorizontalRes {
        byte HR;
        byte HR1;
        byte HR2;
    public HorizontalRes(byte hr1, byte hr2) { 

        this.HR = (byte) ((hr2 & 1) | ((hr1 & 3) << 1));
        this.HR1 = hr1;
        this.HR2 = hr2;

        }

        public float getHR() {
            if (HR2 == 1) {
                return 368f;
            }
            else {
                switch (HR1) {
                    case 0: return 256f;
                    case 1: return 320f;
                    case 2: return 512f;
                    case 3: return 640f;

                }

            }

            return 0f;  

        }
        public UInt32 intoStatus() {      //Bits [18:16] of GPUSTAT
            return ((UInt32)this.HR) << 16;
        }

    }

}
