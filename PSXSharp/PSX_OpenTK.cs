using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using PSXSharp.Core;
using PSXSharp.Peripherals.GPU;
using PSXSharp.Peripherals.IO;
using PSXSharp.Peripherals.MDEC;
using PSXSharp.Peripherals.Timers;
using PSXSharp.Shaders;
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Timers;
using Keys = OpenTK.Windowing.GraphicsLibraryFramework.Keys;

namespace PSXSharp {
    public class PSX_OpenTK {
        public Renderer mainWindow;
        public PSX_OpenTK(string biosPath, string bootPath, bool isBootingEXE) {
            GLFWProvider.CheckForMainThread = false;

            var nativeWindowSettings = new NativeWindowSettings() {
                Size = new Vector2i(1024, 512),
                Title = "OpenGL",
                Flags = ContextFlags.ForwardCompatible,
                APIVersion = Version.Parse("4.6.0"),
                WindowBorder = WindowBorder.Resizable,
            };

            var Gws = GameWindowSettings.Default;
            Gws.RenderFrequency = 00;   
            Gws.UpdateFrequency = 00;
         
            nativeWindowSettings.Location = new Vector2i((1980 - nativeWindowSettings.Size.X) / 2, (1080 - nativeWindowSettings.Size.Y) / 2);

            /*try {
                var windowIcon = new WindowIcon(new OpenTK.Windowing.Common.Input.Image(300, 300, ImageToByteArray(@"PSX logo.jpg")));
                nativeWindowSettings.Icon = windowIcon;
            }
            catch (FileNotFoundException ex) { 
                Console.WriteLine("Warning: PSX logo not found!");
            }*/
            

            mainWindow = new Renderer(Gws, nativeWindowSettings);
            mainWindow.VSync = VSyncMode.Off;

            Console.OutputEncoding = Encoding.UTF8;

            //Create everything here, pass relevant user settings
            RAM Ram = new RAM();
            BIOS Bios = new BIOS(biosPath);
            Scratchpad Scratchpad = new Scratchpad();
            CD_ROM cdrom = isBootingEXE? new CD_ROM() : new CD_ROM(bootPath, false);
            SPU Spu = new SPU(ref cdrom.DataController);         //Needs to read CD-Audio
            DMA Dma = new DMA();
            JOY JOY_IO = new JOY();
            SIO1 SerialIO1 = new SIO1();
            MemoryControl MemoryControl = new MemoryControl();   //useless ?
            RAM_SIZE RamSize = new RAM_SIZE();                   //useless ?
            CACHECONTROL CacheControl = new CACHECONTROL();      //useless ?
            Expansion1 Ex1 = new Expansion1();
            Expansion2 Ex2 = new Expansion2();
            Timer0 Timer0 = new Timer0();
            Timer1 Timer1 = new Timer1();
            Timer2 Timer2 = new Timer2();
            MacroblockDecoder Mdec = new MacroblockDecoder();
            GPU Gpu = new GPU(mainWindow, ref Timer0, ref Timer1);

            BUS Bus = new BUS(          
                Bios,Ram,Scratchpad,cdrom,Spu,Dma,
                JOY_IO, SerialIO1, MemoryControl,RamSize,CacheControl,
                Ex1,Ex2,Timer0,Timer1,Timer2,Mdec,Gpu
                );


            bool isRecompiler = true;
            bool is_x64 = true;
            CPU CPU = CPUWrapper.CreateInstance(isRecompiler, is_x64, isBootingEXE, bootPath, Bus);

            string cpuType = CPUWrapper.GetCPUType();
            mainWindow.MainCPU = CPU;

            mainWindow.Title += " | ";
            mainWindow.Title += cpuType;
            mainWindow.Title += " | ";

            if (bootPath != null) {
                mainWindow.Title += Path.GetFileName(bootPath);
            } else {
                mainWindow.Title += "PSX-BIOS";
            }
            mainWindow.Title += " | ";
            mainWindow.TitleCopy = mainWindow.Title;

            mainWindow.Run();       //Infinite loop 
            mainWindow.FrameTimer.Dispose();
            mainWindow.Dispose();   //Will reach this if the render window 
            mainWindow = null;
            SerialIO1.Dispose();
        }   
    }

    public class Renderer : GameWindow {    //Now it gets really messy 
        //public CPUInterpreter CPU;
        public CPU MainCPU;

        public bool IsEmuPaused; 

        const int VRAM_WIDTH = 1024;
        const int VRAM_HEIGHT = 512;

        private int VertexArrayObject;
        private int VertexBufferObject;
        private int ColorsBuffer;
        private int VramTexture;
        private int VramFrameBuffer;
        private int SampleTexture;
        private int TexCoords;
        private int TexWindow;

        private int IsCopy;
        private int TexModeLoc;
        private int MaskBitSettingLoc;
        private int ClutLoc;
        private int TexPageLoc;

        private int Display_Area_X_Start_Loc;
        private int Display_Area_Y_Start_Loc;
        private int Display_Area_X_End_Loc;
        private int Display_Area_Y_End_Loc;

        private int Aspect_Ratio_X_Offset_Loc;
        private int Aspect_Ratio_Y_Offset_Loc;

        private int TransparencyModeLoc;
        private int IsDitheredLoc;
        private int RenderModeLoc;

        //Signed 11 bits
        private short DrawOffsetX = 0;
        private short DrawOffsetY = 0;


        public bool Is24bpp = false;

        public bool IsUsingMouse = false;
        public bool ShowTextures = true;
        public bool IsFullScreen = false;

        int ScissorBox_X = 0;
        int ScissorBox_Y = 0;
        int ScissorBoxWidth = VRAM_WIDTH;
        int ScissorBoxHeight = VRAM_HEIGHT;

        short[] Vertices;
        byte[] Colors;
        ushort[] UV;

        //This is going to contain blocks that are either clean (0) or dirty (1) for texture invalidation 
        const int IntersectionBlockLength = 64;
        private int[,] IntersectionTable = new int[VRAM_HEIGHT / IntersectionBlockLength, VRAM_WIDTH / IntersectionBlockLength];
        bool FrameUpdated = false;
        Shader Shader;

        public enum RenderMode {
            RenderingPrimitives = 0,                    //Normal mode that games will use to draw primitives
            Rendering16bppFullVram = 1,                 //When drawing the vram on screen
            Rendering16bppAs24bppFullVram = 2,          //When drawing the 16bpp vram as 24bpp
        }

        public Renderer(GameWindowSettings gameWindowSettings, NativeWindowSettings nativeWindowSettings)
             : base(gameWindowSettings, nativeWindowSettings) {

            //Clear the window
            GL.ClearColor(0.0f, 0.0f, 0.0f, 1.0f);
            GL.Clear(ClearBufferMask.ColorBufferBit);
            SwapBuffers();
            SetTimer();
        }

        protected override void OnLoad() {

            //Load shaders 
            Shader = new Shader(Shader.VertixShader, Shader.FragmentShader);
            Shader.Use();

            GL.Viewport(0, 0, this.Size.X, this.Size.Y);
            GL.ClearColor(0.0f, 0.0f, 0.0f, 1.0f);      //This can be ignored as the PS1 BIOS will initially draw a black quad clearing the buffer anyway
            GL.Clear(ClearBufferMask.ColorBufferBit);  
            SwapBuffers();
            
            //Get Locations
            TexWindow = GL.GetUniformLocation(Shader.Program, "u_texWindow");
            TexModeLoc = GL.GetUniformLocation(Shader.Program, "TextureMode");
            ClutLoc = GL.GetUniformLocation(Shader.Program, "inClut");
            TexPageLoc = GL.GetUniformLocation(Shader.Program, "inTexpage");
            IsCopy = GL.GetUniformLocation(Shader.Program, "isCopy");

            TransparencyModeLoc = GL.GetUniformLocation(Shader.Program, "transparencyMode");
            MaskBitSettingLoc = GL.GetUniformLocation(Shader.Program, "maskBitSetting");
            IsDitheredLoc = GL.GetUniformLocation(Shader.Program, "isDithered");
            RenderModeLoc = GL.GetUniformLocation(Shader.Program, "renderMode");

            Display_Area_X_Start_Loc = GL.GetUniformLocation(Shader.Program, "display_area_x_start");
            Display_Area_Y_Start_Loc = GL.GetUniformLocation(Shader.Program, "display_area_y_start");
            Display_Area_X_End_Loc = GL.GetUniformLocation(Shader.Program, "display_area_x_end");
            Display_Area_Y_End_Loc = GL.GetUniformLocation(Shader.Program, "display_area_y_end");

            Aspect_Ratio_X_Offset_Loc = GL.GetUniformLocation(Shader.Program, "aspect_ratio_x_offset");
            Aspect_Ratio_Y_Offset_Loc = GL.GetUniformLocation(Shader.Program, "aspect_ratio_y_offset");

            //Create VAO/VBO/Buffers and Textures
            VertexArrayObject = GL.GenVertexArray();
            VertexBufferObject = GL.GenBuffer();                 
            ColorsBuffer = GL.GenBuffer();
            TexCoords = GL.GenBuffer();
            VramTexture = GL.GenTexture();
            SampleTexture = GL.GenTexture();
            VramFrameBuffer = GL.GenFramebuffer();

            GL.BindVertexArray(VertexArrayObject);

            GL.Enable(EnableCap.Texture2D);

            GL.BindTexture(TextureTarget.Texture2D, VramTexture);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, VRAM_WIDTH, VRAM_HEIGHT, 0, PixelFormat.Bgra, PixelType.UnsignedShort1555Reversed, (IntPtr)null);

            GL.BindTexture(TextureTarget.Texture2D, SampleTexture);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, VRAM_WIDTH, VRAM_HEIGHT, 0, PixelFormat.Bgra, PixelType.UnsignedShort1555Reversed, (IntPtr)null);

         
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, VramFrameBuffer);
            GL.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, VramTexture, 0);
            if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != FramebufferErrorCode.FramebufferComplete) {
                Console.WriteLine("[OpenGL] Uncompleted Frame Buffer !");
            }

            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 2);
            GL.PixelStore(PixelStoreParameter.PackAlignment, 2);
            GL.Uniform1(GL.GetUniformLocation(Shader.Program, "u_vramTex"), 0);
            GL.Uniform1(RenderModeLoc, (int)RenderMode.RenderingPrimitives);

            if (JoystickStates[0] != null) {
                Console.WriteLine($"Controller Name: {JoystickStates[0].Name}");
            }
        }

        public void SetOffset(Int16 x, Int16 y) {
            //Already sign extended
            DrawOffsetX = x; 
            DrawOffsetY = y;   
        }

        ushort TexWindowX;
        ushort TexWindowY;
        ushort TexWindowZ;
        ushort TexWindowW;
        public void SetTextureWindow(ushort x, ushort y, ushort z, ushort w) {
            GL.Uniform4(TexWindow, x, y, z, w);
            TexWindowX = x;
            TexWindowY = y;
            TexWindowZ = z;
            TexWindowW = w;
        }

        public void SetScissorBox(int x, int y, int width, int height) {
            GL.Viewport(0, 0, VRAM_WIDTH, VRAM_HEIGHT);

            ScissorBox_X = x;
            ScissorBox_Y = y;
            ScissorBoxWidth = Math.Max(width + 1, 0);
            ScissorBoxHeight = Math.Max(height + 1, 0);

            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, VramFrameBuffer);

            GL.Enable(EnableCap.ScissorTest);
            GL.Scissor(ScissorBox_X, ScissorBox_Y, ScissorBoxWidth, ScissorBoxHeight);
        }

        public void DrawTrinangle(
            short x1, short y1, 
            short x2, short y2,
            short x3, short y3,
            byte r1, byte g1, byte b1,
            byte r2, byte g2, byte b2,
            byte r3, byte g3, byte b3,
            ushort tx1, ushort ty1,
            ushort tx2, ushort ty2,
            ushort tx3, ushort ty3,
            bool isTextured, ushort clut, ushort page, bool isDithered
            ) {
            
            GL.Viewport(0, 0, VRAM_WIDTH, VRAM_HEIGHT);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, VramFrameBuffer);

            Vertices = new short[]{
             x1,  y1,
             x2,  y2,
             x3,  y3
            };
            Colors = new byte[]{
             r1,  g1,  b1,
             r2,  g2,  b2,
             r3,  g3,  b3,
            };
            UV = new ushort[] {
             tx1, ty1,
             tx2, ty2,
             tx3, ty3
            };

            if(!ApplyDrawingOffset(ref Vertices)) { return; }

            GL.BindBuffer(BufferTarget.ArrayBuffer, VertexBufferObject);
            GL.BufferData(BufferTarget.ArrayBuffer, Vertices.Length * sizeof(short), Vertices, BufferUsageHint.StreamDraw);
            GL.VertexAttribIPointer(0, 2, VertexAttribIntegerType.Short, 0, (IntPtr)null);  //size: 2 for x,y only!
            GL.EnableVertexAttribArray(0);

            GL.BindBuffer(BufferTarget.ArrayBuffer, ColorsBuffer);
            GL.BufferData(BufferTarget.ArrayBuffer, Colors.Length * sizeof(byte), Colors, BufferUsageHint.StreamDraw);
            GL.VertexAttribIPointer(1, 3, VertexAttribIntegerType.UnsignedByte, 0, (IntPtr)null);
            GL.EnableVertexAttribArray(1);

            if (isTextured) {
                GL.Uniform1(ClutLoc, clut);
                GL.Uniform1(TexPageLoc, page);
                GL.Uniform1(TexModeLoc, (page >> 7) & 3);
                GL.BindBuffer(BufferTarget.ArrayBuffer, TexCoords);
                GL.BufferData(BufferTarget.ArrayBuffer, UV.Length * sizeof(ushort), UV, BufferUsageHint.StreamDraw);
                GL.VertexAttribPointer(2, 2, VertexAttribPointerType.UnsignedShort, false, 0, (IntPtr)null);
                GL.EnableVertexAttribArray(2);
               
                if (TextureInvalidatePrimitive(ref UV, page, clut)) {
                    VramSync();
                }

            }
            else {
                GL.Uniform1(TexModeLoc, -1);
                GL.Uniform1(ClutLoc, 0);
                GL.Uniform1(TexPageLoc, 0);
                GL.DisableVertexAttribArray(2);
            }

            GL.Uniform1(IsDitheredLoc, isDithered ? 1 : 0);

            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
            UpdateIntersectionTable(ref Vertices);
            FrameUpdated = true;
        }

        public void DrawRectangle(
            short x1, short y1,
            short x2, short y2,
            short x3, short y3,
            short x4, short y4,
            byte r1, byte g1, byte b1,
            byte r2, byte g2, byte b2,
            byte r3, byte g3, byte b3,
            byte r4, byte g4, byte b4,
            ushort tx1, ushort ty1,
            ushort tx2, ushort ty2,
            ushort tx3, ushort ty3,
            ushort tx4, ushort ty4,

            bool isTextured, ushort clut, ushort texPage, byte texDepth)  {
          
            GL.Viewport(0, 0, VRAM_WIDTH, VRAM_HEIGHT);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, VramFrameBuffer);
            
            Vertices = new short[]{
             x1,  y1,
             x2,  y2,
             x3,  y3,
             x4,  y4,
            };
            Colors = new byte[]{
             r1,  g1,  b1,
             r2,  g2,  b2,
             r3,  g3,  b3,
             r4,  g4,  b4,
            };
            UV = new ushort[] {
             tx1, ty1,
             tx2, ty2,
             tx3, ty3,
             tx4, ty4
            };

            if (!ApplyDrawingOffset(ref Vertices)) { return; }
            GL.BindBuffer(BufferTarget.ArrayBuffer, VertexBufferObject);
            GL.BufferData(BufferTarget.ArrayBuffer, Vertices.Length * sizeof(short), Vertices, BufferUsageHint.StreamDraw);
            GL.VertexAttribIPointer(0, 2, VertexAttribIntegerType.Short, 0, (IntPtr)null);  //size: 2 for x,y only!
            GL.EnableVertexAttribArray(0);

            GL.BindBuffer(BufferTarget.ArrayBuffer, ColorsBuffer);
            GL.BufferData(BufferTarget.ArrayBuffer, Colors.Length * sizeof(byte), Colors, BufferUsageHint.StreamDraw);
            GL.VertexAttribIPointer(1, 3, VertexAttribIntegerType.UnsignedByte, 0, (IntPtr)null);
            GL.EnableVertexAttribArray(1);

            if (isTextured) {
                GL.Uniform1(ClutLoc, clut);
                GL.Uniform1(TexPageLoc, texPage);
                GL.Uniform1(TexModeLoc, texDepth);
                GL.BindBuffer(BufferTarget.ArrayBuffer, TexCoords);
                GL.BufferData(BufferTarget.ArrayBuffer, UV.Length * sizeof(ushort), UV, BufferUsageHint.StreamDraw);
                GL.VertexAttribPointer(2, 2, VertexAttribPointerType.UnsignedShort, false, 0, (IntPtr)null);
                GL.EnableVertexAttribArray(2);
                if (TextureInvalidatePrimitive(ref UV, texPage, clut)) {
                    VramSync();
                }
            }
            else {
                GL.Uniform1(TexModeLoc, -1);
                GL.Uniform1(ClutLoc, 0);
                GL.Uniform1(TexPageLoc, 0);
                GL.DisableVertexAttribArray(2);
            }

            GL.Uniform1(IsDitheredLoc, 0);  //RECTs are NOT dithered

            GL.DrawArrays(PrimitiveType.TriangleFan, 0, 4);
            UpdateIntersectionTable(ref Vertices);
            FrameUpdated = true;
        }

        public void DrawLines(ref short[] vertices, ref byte[] colors, bool isPolyLine, bool isDithered) {
            /*short firstX = vertices[0];
            short firstY = vertices[1];
            short lastX = vertices[vertices.Length - 2];
            short lastY = vertices[vertices.Length - 1];
            bool isWireFrame = (firstX == lastX) && (firstY == lastY);*/

            GL.Viewport(0, 0, VRAM_WIDTH, VRAM_HEIGHT);
            GL.Uniform1(TexModeLoc, -1);
            if (!ApplyDrawingOffset(ref vertices)) { return; }

            GL.BindBuffer(BufferTarget.ArrayBuffer, VertexBufferObject);
            GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(short), vertices, BufferUsageHint.StreamDraw);
            GL.VertexAttribIPointer(0, 2, VertexAttribIntegerType.Short, 0, (IntPtr)null);
            GL.EnableVertexAttribArray(0);

            GL.BindBuffer(BufferTarget.ArrayBuffer, ColorsBuffer);
            GL.BufferData(BufferTarget.ArrayBuffer, colors.Length * sizeof(byte), colors, BufferUsageHint.StreamDraw);
            GL.VertexAttribIPointer(1, 3, VertexAttribIntegerType.UnsignedByte, 0, (IntPtr)null);
            GL.EnableVertexAttribArray(1);

            GL.Uniform1(IsDitheredLoc, isDithered ? 1 : 0);

            GL.DrawArrays(isPolyLine ? PrimitiveType.LineStrip : PrimitiveType.Lines, 0, vertices.Length / 2);
            UpdateIntersectionTable(ref vertices);
            FrameUpdated = true;
        }

        public void ReadBackTexture(int x, int y, int width, int height, ref ushort[] texData) {
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, VramFrameBuffer);
            GL.ReadPixels(x, y, width, height, PixelFormat.Rgba, PixelType.UnsignedShort1555Reversed, texData);
        }

        public void VramFillRectangle(ref GPU_MemoryTransfer transfare) {
            int width = (int)transfare.Width;
            int height = (int)transfare.Height;

            int x = (int)(transfare.Parameters[1] & 0x3F0);
            int y = (int)((transfare.Parameters[1] >> 16) & 0x1FF);

            float r = (transfare.Parameters[0] & 0xFF) / 255.0f;
            float g = ((transfare.Parameters[0] >> 8) & 0xFF) / 255.0f;
            float b = ((transfare.Parameters[0] >> 16) & 0xFF) / 255.0f;


            GL.Viewport(0, 0, VRAM_WIDTH, VRAM_HEIGHT);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, VramFrameBuffer);
            GL.ClearColor(r, g, b, 0.0f);       //alpha = 0 (bit 15)
            GL.Scissor(x, y, width, height);
            GL.Clear(ClearBufferMask.ColorBufferBit);

            short[] rectangle = new short[] {
                (short)x, (short)y,
                (short)(x+width), (short)y,
                (short)(x+width),(short)(y+height),
                (short)x, (short)(y+height)
            };

            UpdateIntersectionTable(ref rectangle);
            
            //update_SamplingTexture();
            
            GL.Scissor(ScissorBox_X, ScissorBox_Y, ScissorBoxWidth, ScissorBoxHeight);
            GL.ClearColor(0, 0, 0, 1.0f);
            FrameUpdated = true;
        }

        public void CpuToVramCopy(ref GPU_MemoryTransfer transfare) {
            int width = (int)transfare.Width;
            int height = (int)transfare.Height;

            int x_dst = (int)(transfare.Parameters[1] & 0x3FF);
            int y_dst = (int)((transfare.Parameters[1] >> 16) & 0x1FF);


            if (MainCPU.GetBUS().GPU.ForceSetMaskBit) {
                for (int i = 0; i < transfare.Data.Length; i++) { transfare.Data[i] |= (1 << 15); }
            }

            //Slow
            /* ushort[] old = new ushort[width * height];
               if (CPU.BUS.GPU.preserve_masked_pixels) {
                 GL.ReadPixels(x, y, width, height, PixelFormat.Rgba, PixelType.UnsignedShort1555Reversed, old);
                 for (int i = 0; i < width * height; i++) {
                     if ((old[i] >> 15) == 1) {
                         textureData[i] = old[i];
                     }
                 }
             } */

            GL.Disable(EnableCap.ScissorTest);

            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, 0);
            GL.BindTexture(TextureTarget.Texture2D, VramTexture);
            GL.TexSubImage2D(TextureTarget.Texture2D, 0, x_dst, y_dst, width, height, 
                PixelFormat.Rgba, PixelType.UnsignedShort1555Reversed, transfare.Data);

            short[] rectangle = new short[] {
                (short)x_dst, (short)y_dst,
                (short)(x_dst+width), (short)y_dst,
                (short)(x_dst+width),(short)(y_dst+height),
                (short)x_dst, (short)(y_dst+height)
            };

            UpdateIntersectionTable(ref rectangle);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, VramFrameBuffer);
            GL.Enable(EnableCap.ScissorTest);
            GL.Scissor(ScissorBox_X, ScissorBox_Y, ScissorBoxWidth, ScissorBoxHeight);
            FrameUpdated = true;
        }

        private void VramSync() {
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, VramFrameBuffer);
            GL.BindTexture(TextureTarget.Texture2D, SampleTexture);
            GL.CopyTexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, 0, 0, VRAM_WIDTH, VRAM_HEIGHT);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, VramFrameBuffer);

            //Reset all blocks to clean
            for (int i = 0; i < VRAM_WIDTH / IntersectionBlockLength; i++) {
                for (int j = 0; j < VRAM_HEIGHT / IntersectionBlockLength; j++) {
                    IntersectionTable[j, i] = 0;
                }
            }
        }

        public void VramToVramCopy(ref GPU_MemoryTransfer transfare) {   
            //This transfare should be subject to mask bit settings

            //Get the dimensions
            int width = (int)transfare.Width;
            int height = (int)transfare.Height;

            int x_src = (int)(transfare.Parameters[1] & 0x3FF);
            int x_dst = (int)(transfare.Parameters[2] & 0x3FF);

            int y_src = (int)((transfare.Parameters[1] >> 16) & 0x1FF);
            int y_dst = (int)((transfare.Parameters[2] >> 16) & 0x1FF);

            //Console.WriteLine($"From: {x_src}, {y_src} to {x_dst}, {y_dst} --- Width: {width} Height: {height}");

            //Set up the verticies
            ushort[] src_coords = new ushort[] {
                (ushort)x_src, (ushort)y_src,
                (ushort)(x_src + width), (ushort)y_src,
                (ushort)(x_src + width), (ushort)(y_src + height),
                (ushort)x_src, (ushort)(y_src + height)
            };

            short[] dst_coords = new short[] {
                (short)x_dst, (short)y_dst,
                (short)(x_dst + width), (short)y_dst,
                (short)(x_dst + width), (short)(y_dst + height),
                (short)x_dst, (short)(y_dst + height)
            };

            //Make sure we sample from an up-to-date texture
            if (TextureInvalidate(ref src_coords)) {
                VramSync();
            }

            //Bind GL stuff
            GL.BindTexture(TextureTarget.Texture2D, SampleTexture);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, VramFrameBuffer);

           /* DisableBlending();  //?
            GL.Disable(EnableCap.ScissorTest);

            GL.BindBuffer(BufferTarget.ArrayBuffer, VertexBufferObject);
            GL.BufferData(BufferTarget.ArrayBuffer, dst_coords.Length * sizeof(short), dst_coords, BufferUsageHint.StreamDraw);
            GL.VertexAttribIPointer(0, 2, VertexAttribIntegerType.Short, 0, (IntPtr)null);
            GL.EnableVertexAttribArray(0);

            GL.DisableVertexAttribArray(1); //No need for colors buffer

            GL.BindBuffer(BufferTarget.ArrayBuffer, TexCoords);
            GL.BufferData(BufferTarget.ArrayBuffer, src_coords.Length * sizeof(ushort), src_coords, BufferUsageHint.StreamDraw);
            GL.VertexAttribPointer(2, 2, VertexAttribPointerType.UnsignedShort, false, 0, (IntPtr)null);
            GL.EnableVertexAttribArray(2);

            //Set up the uniforms
            GL.Uniform1(RenderModeLoc, (int)RenderMode.RenderingPrimitives);
            GL.Uniform1(TexModeLoc, 2);             //Mode = 16bpp (direct)
            GL.Uniform1(IsDitheredLoc, 0);          //No dithering
            GL.Uniform1(IsCopy, 1);                 //For copying this must be set to 1

            //Draw a rectangle to perform the copy
            GL.DrawArrays(PrimitiveType.TriangleFan, 0, 4);    
            UpdateIntersectionTable(ref dst_coords);

            //Restore
            GL.Enable(EnableCap.ScissorTest);
            GL.Scissor(ScissorBox_X, ScissorBox_Y, ScissorBoxWidth, ScissorBoxHeight);
            GL.Uniform1(IsCopy, 0);*/

            //Summary:
            //Instead of GL.CopyImageSubData, I copy the data by drawing a 16bpp textured rectangle at dst coords with its texture coords
            //being the src coords. The reason is that I want it to pass through my shader for the mask bit setting to get handeled.
            //Note that both ways are much faster than GL.ReadPixels.

            GL.CopyImageSubData(
                SampleTexture, ImageTarget.Texture2D, 0, x_src, y_src, 0, 
                VramTexture, ImageTarget.Texture2D, 0, x_dst, y_dst, 0,
                width, height, 1);

            UpdateIntersectionTable(ref dst_coords);
            FrameUpdated = true;

        }

        public void VramToCpuCopy(ref GPU_MemoryTransfer transfare) {
            int width = (int)transfare.Width;
            int height = (int)transfare.Height;

            int x_src = (int)(transfare.Parameters[1] & 0x3FF);
            int y_src = (int)((transfare.Parameters[1] >> 16) & 0x1FF);

            ReadBackTexture(x_src, y_src, width, height, ref transfare.Data);
        }

        internal void SetBlendingFunction(uint function) {

            GL.Uniform1(TransparencyModeLoc, (int)function);
            //Console.WriteLine("Transparency: " +  function);

            GL.Enable(EnableCap.Blend);
            //B = Destination
            //F = Source
            GL.BlendFunc(BlendingFactor.Src1Color, BlendingFactor.Src1Alpha);        //Alpha values are handled in GLSL
            GL.BlendEquation(function == 2? BlendEquationMode.FuncReverseSubtract : BlendEquationMode.FuncAdd);
        }

        internal void MaskBitSetting(int setting) {
            GL.Uniform1(MaskBitSettingLoc, setting);
        }

        public bool TextureInvalidatePrimitive(ref ushort[] uv, uint texPage, uint clut) {
            //Experimental 
            //Checks whether the textured primitive is reading from a dirty block

            //Hack: Always sync if preserve_masked_pixels is true
            //This is kind of slow but fixes Silent Hills 
            if (MainCPU.GetBUS().GPU.PreserveMaskedPixels) {
                return true;
            }

            int mode = (int)((texPage >> 7) & 3);
            uint divider = (uint)(4 >> mode);
           
            uint smallestX = 1023;
            uint smallestY = 511;
            uint largestX = 0;
            uint largestY = 0;

            for (int i = 0; i < uv.Length; i += 2) {
                largestX = Math.Max(largestX, uv[i]);   
                smallestX = Math.Min(smallestX, uv[i]);
            }

            for (int i = 1; i < uv.Length; i += 2) {
                largestY = Math.Max(largestY, uv[i]);
                smallestY = Math.Min(smallestY, uv[i]);
            }

            smallestX = Math.Min(smallestX, 1023);
            smallestY = Math.Min(smallestY, 511);
            largestX = Math.Min(largestX, 1023);
            largestY = Math.Min(largestY, 511);

            uint texBaseX = (texPage & 0xF) * 64;
            uint texBaseY = ((texPage >> 4) & 1) * 256;

            uint width = (largestX - smallestX) / divider;
            uint height = (largestY - smallestY) / divider;

            uint left =  texBaseX / IntersectionBlockLength;
            uint right = ((texBaseX + width) & 0x3FF) / IntersectionBlockLength;           
            uint up = texBaseY / IntersectionBlockLength;
            uint down = ((texBaseY + height) & 0x1FF) / IntersectionBlockLength;         

            //ANDing with 7,15 take cares of vram access wrap when reading textures (same effect as mod 8,16)  
            for (uint y = up; y != ((down + 1) & 0x7); y = (y + 1) & 0x7) {
                for (uint x = left; x != ((right + 1) & 0xF); x = (x + 1) & 0xF) {
                    if (IntersectionTable[y, x] == 1) { return true; }
                }
            }

            //For 4/8 bpp modes we need to check the clut table 
            if (mode == 0 || mode == 1) {
                uint clutX = (clut & 0x3F) * 16;
                uint clutY = ((clut >> 6) & 0x1FF);
                left = clutX / IntersectionBlockLength;               
                up = clutY / IntersectionBlockLength;             //One line 
                for (uint x = left; x < VRAM_WIDTH / IntersectionBlockLength; x++) {
                    if (IntersectionTable[up, x] == 1) { return true; }
                }
            }

            return false;
        }

        public bool TextureInvalidate(ref ushort[] coords) {       
            //Hack: Always sync if preserve_masked_pixels is true
            //This is kind of slow but fixes Silent Hills 
            if (MainCPU.GetBUS().GPU.PreserveMaskedPixels) {
                return true;
            }

            uint smallestX = 1023;
            uint smallestY = 511;
            uint largestX = 0;
            uint largestY = 0;

            for (int i = 0; i < coords.Length; i += 2) {
                largestX = Math.Max(largestX, coords[i]);
                smallestX = Math.Min(smallestX, coords[i]);
            }

            for (int i = 1; i < coords.Length; i += 2) {
                largestY = Math.Max(largestY, coords[i]);
                smallestY = Math.Min(smallestY, coords[i]);
            }

            smallestX = Math.Min(smallestX, 1023);
            smallestY = Math.Min(smallestY, 511);
            largestX = Math.Min(largestX, 1023);
            largestY = Math.Min(largestY, 511);

            uint width = (largestX - smallestX);
            uint height = (largestY - smallestY);

            uint left = smallestX / IntersectionBlockLength;
            uint right = ((smallestX + width) & 0x3FF) / IntersectionBlockLength;
            uint up = smallestY / IntersectionBlockLength;
            uint down = ((smallestY + height) & 0x1FF) / IntersectionBlockLength;

            //ANDing with 7,15 take cares of vram access wrap when reading textures (same effect as mod 8,16)  
            for (uint y = up; y != ((down + 1) & 0x7); y = (y + 1) & 0x7) {
                for (uint x = left; x != ((right + 1) & 0xF); x = (x + 1) & 0xF) {
                    if (IntersectionTable[y, x] == 1) { return true; }
                }
            }
            return false;
        }


        public void UpdateIntersectionTable(ref short[] vertices) {
            //Mark any affected blocks as dirty
            int smallestX = 1023;
            int smallestY = 511;
            int largestX = -1024;
            int largestY = -512;

            for (int i = 0; i < vertices.Length; i += 2) {
                largestX = Math.Max(largestX, vertices[i]);
                smallestX = Math.Min(smallestX, vertices[i]);
            }

            for (int i = 1; i < vertices.Length; i += 2) {
                largestY = Math.Max(largestY, vertices[i]);
                smallestY = Math.Min(smallestY, vertices[i]);
            }

            smallestX = Math.Clamp(smallestX, 0, 1023);
            smallestY = Math.Clamp(smallestY, 0, 511);
            largestX = Math.Clamp(largestX, 0, 1023);
            largestY = Math.Clamp(largestY, 0, 511);

            int left = smallestX / IntersectionBlockLength;
            int right = largestX / IntersectionBlockLength;        
            int up = smallestY  / IntersectionBlockLength;
            int down = largestY / IntersectionBlockLength;         

            //No access wrap for drawing, anything out of bounds is clamped 
            for (int y = up; y <= down; y++) {
                for (int x = left; x <= right; x++) {
                    IntersectionTable[y, x] = 1;
                }
            }
        }
        
        private short Signed11Bits(ushort input) {
            return (short)(((short)(input << 5)) >> 5);
        }

        //Applies Drawing offset and checks if final dimensions are valid (within range)
        private bool ApplyDrawingOffset(ref short[] vertices) {
            short maxX = -1024;
            short maxY = -1024;
            short minX = 1023;
            short minY = 1023;

            for (int i = 0; i < vertices.Length; i += 2) {
                //vertices[i] = Signed11Bits((ushort)(Signed11Bits((ushort)vertices[i]) + DrawOffsetX));
                vertices[i] = (short)(Signed11Bits((ushort)vertices[i]) + DrawOffsetX);
              
                maxX = Math.Max(maxX, vertices[i]);
                minX = Math.Min(minX, vertices[i]);                 
            }

            for (int i = 1; i < vertices.Length; i += 2) {
                //vertices[i] = Signed11Bits((ushort)(Signed11Bits((ushort)vertices[i]) + DrawOffsetY));
                vertices[i] = (short)(Signed11Bits((ushort)vertices[i]) + DrawOffsetY);

                maxY = Math.Max(maxY, vertices[i]);
                minY = Math.Min(minY, vertices[i]);
            }

            return !((Math.Abs(maxX - minX) > 1024) || (Math.Abs(maxY - minY) > 512));
        }

        public System.Timers.Timer FrameTimer;
        public int Frames = 0;
        public string TitleCopy;



        public void Display() {
            DisplayFrame();
            SwapBuffers();
            if (FrameUpdated) {
                Frames++;
                FrameUpdated = false;
            }     
        }


        private void SetTimer() {
            // Create a timer with a 1 second interval.
            FrameTimer = new System.Timers.Timer(1000);
            // Hook up the Elapsed event for the timer. 
            FrameTimer.Elapsed += OnTimedEvent;
            FrameTimer.AutoReset = true;
            FrameTimer.Enabled = true;
            //TimerLoop();
        }

        private void OnTimedEvent(Object source, ElapsedEventArgs e) {
           this.Title = TitleCopy + "FPS: " + Frames + " | CPU: " + MainCPU.GetSpeed().ToString("00.00") + "%";
           Frames = 0;
        }

       /* private async Task TimerLoop() {
            while (true) {
                await Task.Delay(1000); // Sample every second
                double elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
                stopwatch.Restart(); // Reset for next interval

                double cycles = MainCPU.GetSpeed();
                double speed = (cycles / (33868800 * elapsedSeconds)) * 100;

                this.Title = TitleCopy + $" FPS: {Frames} | CPU: {speed:0.00}%";
                Frames = 0;
            }
        }*/

        void DisplayFrame() {
            GL.Disable(EnableCap.ScissorTest);
            DisableBlending();

            GL.Enable(EnableCap.Texture2D);
            GL.DisableVertexAttribArray(1);
            GL.DisableVertexAttribArray(2);

            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, VramFrameBuffer);

            if (Is24bpp) {
                GL.Uniform1(RenderModeLoc, (int)RenderMode.Rendering16bppAs24bppFullVram);            
            } else {
                GL.Uniform1(RenderModeLoc, (int)RenderMode.Rendering16bppFullVram);
            }

            GL.Viewport(0, 0, this.Size.X, this.Size.Y);

            //Disable the ScissorTest and unbind the FBO to draw the entire vram texture to the screen

            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, 0);
            GL.BindTexture(TextureTarget.Texture2D, VramTexture);

            SetAspectRatio();

            GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);

            //Enable ScissorTest and bind FBO for next draws 
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, VramFrameBuffer);
            GL.Enable(EnableCap.ScissorTest);

            GL.BindTexture(TextureTarget.Texture2D, SampleTexture);
            GL.Scissor(ScissorBox_X, ScissorBox_Y, ScissorBoxWidth, ScissorBoxHeight);
            GL.Uniform1(RenderModeLoc, (int)RenderMode.RenderingPrimitives);

        }

        public void DisableBlending() {
            ///GL.Disable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.One, BlendingFactor.Zero);
            GL.BlendEquation(BlendEquationMode.FuncAdd);
            GL.Uniform1(TransparencyModeLoc, 4);    //0-3 for the functions, 4 = disabled
        }

        public void SetAspectRatio() {
            float display_x_start = MainCPU.GetBUS().GPU.DisplayVramXStart;
            float display_y_start = MainCPU.GetBUS().GPU.DisplayVramYStart;

            float display_x_end = MainCPU.GetBUS().GPU.HorizontalRange + display_x_start - 1;   
            float display_y_end = MainCPU.GetBUS().GPU.VerticalRange + display_y_start - 1;

            float width = MainCPU.GetBUS().GPU.HorizontalRange;
            float height = MainCPU.GetBUS().GPU.VerticalRange;

            if (!ShowTextures) {

                GL.Uniform1(Display_Area_X_Start_Loc, display_x_start / VRAM_WIDTH);
                GL.Uniform1(Display_Area_Y_Start_Loc, display_y_start / VRAM_HEIGHT);
                GL.Uniform1(Display_Area_X_End_Loc, display_x_end / VRAM_WIDTH);
                GL.Uniform1(Display_Area_Y_End_Loc, display_y_end / VRAM_HEIGHT);

                if ((width / height) < ((float)this.Size.X / Size.Y)) {

                    //Random formula by JyAli                  
                    float newWidth = (width / height) * Size.Y;                 //Get the new width after stretching 
                    float offset = (Size.X - newWidth) / this.Size.X;           //Calculate the offset and convert it to [0,2]

                    GL.Uniform1(Aspect_Ratio_Y_Offset_Loc, 0.0f);
                    GL.Uniform1(Aspect_Ratio_X_Offset_Loc, offset);

                    GL.Enable(EnableCap.ScissorTest);
                    GL.ClearColor(0.0f, 0.0f, 0.0f, 1.0f);
                    GL.Scissor(0, 0, this.Size.X, this.Size.Y);
                    GL.Clear(ClearBufferMask.ColorBufferBit);
                    GL.Disable(EnableCap.ScissorTest);

                    GL.Scissor(ScissorBox_X, ScissorBox_Y, ScissorBoxWidth, ScissorBoxHeight);

                } else if ((width / height) > ((float)this.Size.X / this.Size.Y)) {

                    //Random formula by JyAli                  
                    float newHeight = (height / width) * Size.X;                 //Get the new height after stretching 
                    float offset = (Size.Y - newHeight) / this.Size.Y;           //Calculate the offset and convert it to [0,2]

                    GL.Uniform1(Aspect_Ratio_Y_Offset_Loc, offset);
                    GL.Uniform1(Aspect_Ratio_X_Offset_Loc, 0.0f);

                    GL.Enable(EnableCap.ScissorTest);
                    GL.ClearColor(0.0f, 0.0f, 0.0f, 1.0f);
                    GL.Scissor(0, 0, this.Size.X, this.Size.Y);
                    GL.Clear(ClearBufferMask.ColorBufferBit);
                    GL.Disable(EnableCap.ScissorTest);

                    GL.Scissor(ScissorBox_X, ScissorBox_Y, ScissorBoxWidth, ScissorBoxHeight);

                } else {
                    GL.Uniform1(Aspect_Ratio_X_Offset_Loc, 0.0f);
                    GL.Uniform1(Aspect_Ratio_Y_Offset_Loc, 0.0f);
                }
            } else {
                //Set the values to display the whole VRAM
                GL.Uniform1(Aspect_Ratio_X_Offset_Loc, 0.0f);
                GL.Uniform1(Aspect_Ratio_Y_Offset_Loc, 0.0f);
                GL.Uniform1(Display_Area_X_Start_Loc, 0.0f);
                GL.Uniform1(Display_Area_Y_Start_Loc, 0.0f);
                GL.Uniform1(Display_Area_X_End_Loc, 1.0f);
                GL.Uniform1(Display_Area_Y_End_Loc, 1.0f);
            }

        }
        protected override void OnResize(ResizeEventArgs e) {
            base.OnResize(e);
            GL.Viewport(0, 0, this.Size.X, this.Size.Y);
            SwapBuffers();
        }

        protected override void OnKeyDown(KeyboardKeyEventArgs e) {
            base.OnKeyDown(e);
            ConsoleColor previousColor = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.White;

            if (e.Key.Equals(Keys.Escape)) {
                Close();

            } else if (e.Key.Equals(Keys.D)) {
                Console.WriteLine("Toggle Debug");
                MainCPU.GetBUS().debug = !MainCPU.GetBUS().debug;
                Thread.Sleep(100);

            } else if (e.Key.Equals(Keys.P)) {
                IsEmuPaused = !IsEmuPaused;
                Thread.Sleep(100);

            } else if (e.Key.Equals(Keys.Tab)) {
                ShowTextures = !ShowTextures;
                Thread.Sleep(100);

            } else if (e.Key.Equals(Keys.F)) {
                IsFullScreen = !IsFullScreen;
                this.WindowState = IsFullScreen ? WindowState.Fullscreen : WindowState.Normal;
                this.CursorState = IsFullScreen ? CursorState.Hidden : CursorState.Normal;
                Thread.Sleep(100);

            } else if (e.Key.Equals(Keys.F1)) {
                /*Console.WriteLine("Dumping memory...");
                File.WriteAllBytes("MemoryDump.bin", MainCPU.GetBUS().RAM.GetMemoryPointer());
                Console.WriteLine("Done!");
                Thread.Sleep(100);*/

            } else if (e.Key.Equals(Keys.F2)) {
                Console.WriteLine("Resetting...");
                MainCPU.Reset();

                Thread.Sleep(100);

            } else if (e.Key.Equals(Keys.C)) {
                MainCPU.GetBUS().JOY_IO.Controller1.IgnoreInput = !MainCPU.GetBUS().JOY_IO.Controller1.IgnoreInput;
                if (MainCPU.GetBUS().JOY_IO.Controller1.IgnoreInput) {
                    Console.WriteLine("Controller inputs ignored");
                } else {
                    Console.WriteLine("Controller inputs not ignored");
                }
                Thread.Sleep(100);

            } else if (e.Key.Equals(Keys.K)) {
                /* Does not work on this thread. TODO: Move to main thread with the UI
                 //We borrow some functionality from Windows Forms
                System.Windows.Forms.FolderBrowserDialog folderBrowserDialog = new System.Windows.Forms.FolderBrowserDialog();
                folderBrowserDialog.Description = "Please Select a Game Folder to Swap";
                folderBrowserDialog.UseDescriptionForTitle = true;
                if (folderBrowserDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) {
                    Console.WriteLine("Swapping with: " + Path.GetFileName(folderBrowserDialog.SelectedPath));
                    CPU.BUS.CDROM.SwapDisk(folderBrowserDialog.SelectedPath);
                }
                Thread.Sleep(100);
                 */
            }

            Console.ForegroundColor = previousColor;
        }

        protected override void OnUpdateFrame(FrameEventArgs args) {
            base.OnUpdateFrame(args);

            if (IsEmuPaused) {
                return;
            }

            //Clock the CPU
            MainCPU.TickFrame();

            /*try {
                MainCPU.TickFrame();
            } catch(Exception e) {
                Console.WriteLine(e.Message);
                Console.WriteLine(e.StackTrace);
                if (MainCPU.GetType() == typeof(CPU_x64_Recompiler)) {
                    CPU_x64_Recompiler cpu = (CPU_x64_Recompiler)MainCPU;
                    cpu.Dispose();      //Ensure to call dispose to free memory
                    Close();
                    throw new Exception();
                }
            }*/

            //CPU.BUS.SerialIO1.CheckRemoteEnd();

            //Read controller input 
            MainCPU.GetBUS().JOY_IO.Controller1.ReadInput(JoystickStates[0]);
        }
      
        protected override void OnUnload() {
            CPUWrapper.DisposeCPU();

            // Unbind all the resources by binding the targets to 0/null.
            // Unbind all the resources by binding the targets to 0/null.
            GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
            GL.BindTexture(TextureTarget.Texture2D, 0);
            GL.BindVertexArray(0);
            GL.UseProgram(0);

            // Delete all the resources.
            GL.DeleteBuffer(VertexBufferObject);
            GL.DeleteBuffer(ColorsBuffer);
            GL.DeleteBuffer(TexCoords);
            GL.DeleteVertexArray(VertexArrayObject);
            GL.DeleteFramebuffer(VramFrameBuffer);
            GL.DeleteTexture(VramTexture);
            GL.DeleteTexture(SampleTexture);
            GL.DeleteProgram(Shader.Program);
            base.OnUnload();
        }
    }

}
