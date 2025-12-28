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
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Timers;
using Keys = OpenTK.Windowing.GraphicsLibraryFramework.Keys;

namespace PSXSharp {
    public class PSX_OpenTK {
        public Renderer mainWindow;
        public PSX_OpenTK(string biosPath, string bootPath, bool isBootingEXE) {
            //Disable CheckForMainThread to allow running from a secondary thread
            GLFWProvider.CheckForMainThread = false;
            
            var nativeWindowSettings = new NativeWindowSettings() {
                Size = new Vector2i(1024, 512),
                Title = "OpenGL",
                Flags = ContextFlags.ForwardCompatible,
                APIVersion = Version.Parse("4.6.0"),
                WindowBorder = WindowBorder.Resizable,
            };

            nativeWindowSettings.Location = AtCenterOfScreen(nativeWindowSettings.Size);

            var Gws = GameWindowSettings.Default;
            Gws.RenderFrequency = 60;   
            Gws.UpdateFrequency = 60;

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

        public static Vector2i AtCenterOfScreen(Vector2i size) {
            //Get screen resolution 
            var videoMode = Monitors.GetPrimaryMonitor().CurrentVideoMode;
            int width = videoMode.Width;
            int height = videoMode.Height;
            int newX = (width - size.X) / 2;
            int newY = (height - size.Y) / 2;
            return new Vector2i(newX, newY);
        }
    }

    public class Renderer : GameWindow {    //Now it gets really messy 
        public CPU MainCPU;
        public bool IsEmuPaused;

        //Locations
        private int VertexArrayObject;
        private int VertexBufferObject;
        private int VramTexture;
        private int VramFrameBuffer;
        private int SampleTexture;
        private int TexWindowLoc;
        private int IsCopy;
        private int MaskBitSettingLoc;
        private int RenderModeLoc;

        private int Display_Area_X_Start_Loc;
        private int Display_Area_Y_Start_Loc;
        private int Display_Area_X_End_Loc;
        private int Display_Area_Y_End_Loc;

        private int Aspect_Ratio_X_Offset_Loc;
        private int Aspect_Ratio_Y_Offset_Loc;

        //General stuff
        Shader Shader;
        public bool IsUsingMouse = false;
        public bool ShowTextures = true;
        public bool IsFullScreen = false;

        //Texture invalidation 
        private const int IntersectionBlockLength = 64;
        private int[,] IntersectionTable = new int[VRAM_HEIGHT / IntersectionBlockLength, VRAM_WIDTH / IntersectionBlockLength];
        private bool FrameUpdated = false;

        //Vertex info buffer
        private const int MAX_VERTICES = 5000;
        private readonly VertexInfo[] VertexBuffer = new VertexInfo[MAX_VERTICES];
        private int VertexInfoIndex = 0;
        PrimitiveType CurrentBatchType = PrimitiveType.Triangles;

        //Primitive Settings
        readonly int SizeOfVertexInfo = Marshal.SizeOf<VertexInfo>();
        readonly int Stride = Marshal.SizeOf<VertexInfo>();
        readonly nint PositionOffset = Marshal.OffsetOf<VertexInfo>("Position");
        readonly nint ColorOffset = Marshal.OffsetOf<VertexInfo>("Color");
        readonly nint UVOffset = Marshal.OffsetOf<VertexInfo>("UV");
        readonly nint ClutOffset = Marshal.OffsetOf<VertexInfo>("Clut");
        readonly nint TexPageOffset = Marshal.OffsetOf<VertexInfo>("TexPage");
        readonly nint TexModeOffset = Marshal.OffsetOf<VertexInfo>("TextureMode");
        readonly nint DitherOffset = Marshal.OffsetOf<VertexInfo>("IsDithered");
        readonly nint TransOffset = Marshal.OffsetOf<VertexInfo>("TransparencyMode");


        //Global Settings
        private int MaskBitSetting;
        private int ScissorBox_X = 0;
        private int ScissorBox_Y = 0;
        private int ScissorBoxWidth = VRAM_WIDTH;
        private int ScissorBoxHeight = VRAM_HEIGHT;
        private ushort TexWindowX;
        private ushort TexWindowY;
        private ushort TexWindowZ;
        private ushort TexWindowW;
        private short DrawOffsetX = 0; //Signed 11 bits
        private short DrawOffsetY = 0; //Signed 11 bits
        public bool Is24bpp = false;

        //Constants
        private const int VRAM_WIDTH = 1024;
        private const int VRAM_HEIGHT = 512;

        private const int VERTEX_ELEMENTS = 2;  //X, Y
        private const int UV_ELEMENTS = 2;      //U, V
        private const int COLOR_ELEMENTS = 3;   //R, G, B
        private const int REVERSE_SUBTRACT = 2; //B - F Transparency Mode

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
            Shader = new Shader(Shader.VertexShader, Shader.FragmentShader);
            Shader.Use();

            GL.Viewport(0, 0, this.Size.X, this.Size.Y);
            GL.ClearColor(0.0f, 0.0f, 0.0f, 1.0f);      //This can be ignored as the PS1 BIOS will initially draw a black quad clearing the buffer anyway
            GL.Clear(ClearBufferMask.ColorBufferBit);  
            SwapBuffers();
            
            //Get Locations
            TexWindowLoc = GL.GetUniformLocation(Shader.Program, "u_texWindow");
            IsCopy = GL.GetUniformLocation(Shader.Program, "isCopy");
            MaskBitSettingLoc = GL.GetUniformLocation(Shader.Program, "maskBitSetting");
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
                throw new Exception("[OpenGL] Uncompleted Frame Buffer !");
            }

            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 2);
            GL.PixelStore(PixelStoreParameter.PackAlignment, 2);
            GL.Uniform1(GL.GetUniformLocation(Shader.Program, "u_vramTex"), 0);
            GL.Uniform1(RenderModeLoc, (int)RenderMode.RenderingPrimitives);
            SetPSXDrawingSettings();

            if (JoystickStates[0] != null) {
                Console.WriteLine($"Controller Name: {JoystickStates[0].Name}");
            }
        }

        public void SetPSXDrawingSettings() {
            /*
             Important note:
             Data for an array specified by VertexAttribPointer will be converted to floating-point by normalizing if normalized is TRUE, 
             and converted directly to floating-point otherwise. 
             Data for an array specified by VertexAttribIPointer will always be left as integer values; such data are referred to as pure integers.
             
             in the shader:
                VertexAttribIPointer must use int / ivec / uvec
                VertexAttribPointer must use float / vec
            */


            GL.Viewport(0, 0, VRAM_WIDTH, VRAM_HEIGHT);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, VramFrameBuffer);
            BindVertexInfo();

            // Position (passed as is, and converted to floats manually in the shader)
            GL.VertexAttribIPointer(0, VERTEX_ELEMENTS, VertexAttribIntegerType.Short, Stride, PositionOffset); 
            GL.EnableVertexAttribArray(0);

            // Colors (VertexAttribPointer with normalized = true converts to floats and normlizes to [0.0f, 1.0f])
            GL.VertexAttribPointer(1, COLOR_ELEMENTS, VertexAttribPointerType.UnsignedByte, true, Stride, ColorOffset);
            GL.EnableVertexAttribArray(1);

            // UV  (VertexAttribPointer with normalized = false only converts to floats)
            GL.VertexAttribPointer(2, UV_ELEMENTS, VertexAttribPointerType.UnsignedShort, false, Stride, UVOffset);
            GL.EnableVertexAttribArray(2);

            // Clut
            GL.VertexAttribIPointer(3, 1, VertexAttribIntegerType.Int, Stride, ClutOffset);
            GL.EnableVertexAttribArray(3);

            // TexPage
            GL.VertexAttribIPointer(4, 1, VertexAttribIntegerType.Int, Stride, TexPageOffset);
            GL.EnableVertexAttribArray(4);

            // TextureMode
            GL.VertexAttribIPointer(5, 1, VertexAttribIntegerType.Int, Stride, TexModeOffset);
            GL.EnableVertexAttribArray(5);

            // IsDithered
            GL.VertexAttribIPointer(6, 1, VertexAttribIntegerType.Int, Stride, DitherOffset);
            GL.EnableVertexAttribArray(6);

            // TransparencyMode
            GL.VertexAttribIPointer(7, 1, VertexAttribIntegerType.Int, Stride, TransOffset);
            GL.EnableVertexAttribArray(7);

            EnableBlending();
        }

        public void BindVertexInfo() {
            GL.BindBuffer(BufferTarget.ArrayBuffer, VertexBufferObject);
            GL.BufferData(BufferTarget.ArrayBuffer, VertexInfoIndex * SizeOfVertexInfo, VertexBuffer, BufferUsageHint.StreamDraw);
        }

        public void EnsureEnoughBufferSpace(int numberOfVertices) {
            if (VertexInfoIndex + numberOfVertices >= MAX_VERTICES) {
                RenderBatch();
            }
        }

        public void SetBatchType(PrimitiveType batchType) {
            if (CurrentBatchType != batchType) {
                RenderBatch();
                CurrentBatchType = batchType;
            }
        }

        public void EnableBlending() {
            //B = Destination
            //F = Source
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.Src1Color, BlendingFactor.Src1Alpha);        //Alpha values are handled in GLSL
            GL.BlendEquation(BlendEquationMode.FuncAdd);
        }

        public void SetOffset(short x, short y) {
            //Already sign extended
            //Draw offset is handled in ApplyDrawingOffset
            DrawOffsetX = x;
            DrawOffsetY = y;
        }

        public void SetTextureWindow(ushort x, ushort y, ushort z, ushort w) {
            if (x != TexWindowX || y != TexWindowY || z != TexWindowZ || w != TexWindowW) {
                RenderBatch();
                GL.Uniform4(TexWindowLoc, x, y, z, w);
                TexWindowX = x;
                TexWindowY = y;
                TexWindowZ = z;
                TexWindowW = w;
            }
        }

        public void SetScissorBox(int x, int y, int width, int height) {
            if (x != ScissorBox_X || y != ScissorBox_Y || width != ScissorBoxWidth || height != ScissorBoxHeight) {
                RenderBatch();
                GL.Viewport(0, 0, VRAM_WIDTH, VRAM_HEIGHT);

                ScissorBox_X = x;
                ScissorBox_Y = y;
                ScissorBoxWidth = Math.Max(width + 1, 0);
                ScissorBoxHeight = Math.Max(height + 1, 0);

                GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, VramFrameBuffer);

                GL.Enable(EnableCap.ScissorTest);
                GL.Scissor(ScissorBox_X, ScissorBox_Y, ScissorBoxWidth, ScissorBoxHeight);
            }
        }

        public void SetMaskBitSetting(int setting) {
            if (setting != MaskBitSetting) {
                RenderBatch();
                GL.Uniform1(MaskBitSettingLoc, setting);
                MaskBitSetting = setting;
            }
        }

        public void RenderBatch() {
            if (VertexInfoIndex == 0) { return; }

            //We need to rebind the vertex info buffer
            BindVertexInfo();

            GL.DrawArrays(CurrentBatchType, 0, VertexInfoIndex);

            //Console.WriteLine($"Batch Count = {VertexInfoIndex}");
            VertexInfoIndex = 0;
        }

        //This function can handle more than one triangle
        public void DrawTrinangles(Span<short> vertices, ReadOnlySpan<byte> colors, ReadOnlySpan<ushort> uv, 
            bool isTextured, ushort clut, ushort texPage, int textureMode, bool isDithered, int transMode) {
            if (!ApplyDrawingOffset(ref vertices)) { return; }

            EnsureEnoughBufferSpace(vertices.Length / VERTEX_ELEMENTS);
            SetBatchType(PrimitiveType.Triangles);

            //Sync vram if texture is dirty or if it's using B - F transparency mode
            bool needSync = (isTextured && TextureInvalidatePrimitive(uv, texPage, clut)) || transMode == REVERSE_SUBTRACT; 
            if (needSync) {
                VramSync();
            }

            int ditheringValue = isDithered ? 1 : 0;
            int elementIndex = 0;          //Position and UV
            int colorIndex = 0;            //Color

            for (; elementIndex < vertices.Length; elementIndex += VERTEX_ELEMENTS, colorIndex += COLOR_ELEMENTS) {
                ReadOnlySpan<short> currentPosition = vertices.Slice(elementIndex, VERTEX_ELEMENTS);
                ReadOnlySpan<ushort> currentUV = uv.Slice(elementIndex, UV_ELEMENTS);
                ReadOnlySpan<byte> currentColor = colors.Slice(colorIndex, COLOR_ELEMENTS);
                AddVertex(currentPosition, currentColor, currentUV, clut, texPage, textureMode, ditheringValue, transMode);
            }

            UpdateIntersectionTable(vertices);
            FrameUpdated = true;
        }

        //Unused
        public void DrawRectangle(Span<short> vertices, ReadOnlySpan<byte> colors, ReadOnlySpan<ushort> uv,
            bool isTextured, ushort clut, ushort texPage, byte texDepth)  {

            if (!ApplyDrawingOffset(ref vertices)) { return; }

            EnsureEnoughBufferSpace(vertices.Length / VERTEX_ELEMENTS);
            SetBatchType(PrimitiveType.TriangleFan); //I use TriangleFan for rectangles 

            int textureMode = -1;
            if (isTextured) {
                textureMode = texDepth;
                if (TextureInvalidatePrimitive(uv, texPage, clut)) {
                    VramSync();
                }
            }

            int elementIndex = 0;          //Position and UV
            int colorIndex = 0;            //Color
            const int ditheringValue = 0;  //Rectangles are NOT dithered

            for (; elementIndex < vertices.Length; elementIndex += VERTEX_ELEMENTS, colorIndex += COLOR_ELEMENTS) {
                ReadOnlySpan<short> currentPosition = vertices.Slice(elementIndex, VERTEX_ELEMENTS);
                ReadOnlySpan<ushort> currentUV = uv.Slice(elementIndex, UV_ELEMENTS);
                ReadOnlySpan<byte> currentColor = colors.Slice(colorIndex, COLOR_ELEMENTS);
                AddVertex(currentPosition, currentColor, currentUV, clut, texPage, textureMode, ditheringValue, 0);
            }

            UpdateIntersectionTable(vertices);
            FrameUpdated = true;
        }

        public void DrawLines(Span<short> vertices, ReadOnlySpan<byte> colors, bool isPolyLine, bool isDithered, int transMode) {
            if (!ApplyDrawingOffset(ref vertices)) { return; }

            PrimitiveType commandLinesType = isPolyLine ? PrimitiveType.LineStrip : PrimitiveType.Lines;
            EnsureEnoughBufferSpace(vertices.Length / VERTEX_ELEMENTS);
            SetBatchType(commandLinesType);

            //Sync vram if it's using B - F transparency mode
            bool needSync = transMode == REVERSE_SUBTRACT;
            if (needSync) {
                VramSync();
            }

            //No textures
            const int textureMode = -1;
            const int texPage = 0;
            const int clut = 0;
            ReadOnlySpan<ushort> uv = [0, 0];

            int ditheringValue = isDithered ? 1 : 0;    
            int elementIndex = 0;                       //Position
            int colorIndex = 0;                         //Color
              
            for (; elementIndex < vertices.Length; elementIndex += VERTEX_ELEMENTS, colorIndex += COLOR_ELEMENTS) {
                ReadOnlySpan<short> vertex = vertices.Slice(elementIndex, VERTEX_ELEMENTS);
                ReadOnlySpan<byte> color = colors.Slice(colorIndex, COLOR_ELEMENTS);
                AddVertex(vertex, color, uv, clut, texPage, textureMode, ditheringValue, transMode);
            }

            //Don't batch LineStrips because this will result in connecting all of the different strips
            if (commandLinesType == PrimitiveType.LineStrip) {
                RenderBatch();
            }

            UpdateIntersectionTable(vertices);
            FrameUpdated = true;
        }

        public void AddVertex(ReadOnlySpan<short> positionSpan, ReadOnlySpan<byte> colorSpan, ReadOnlySpan<ushort> uvSpan,
            int clut, int texPage, int texMode, int isDithered, int transMode) {
            VertexBuffer[VertexInfoIndex++] = new VertexInfo {
                Position = Position.FromSpan(positionSpan),
                Color = Color.FromSpan(colorSpan),
                UV = UV.FromSpan(uvSpan),
                Clut = clut,
                TexPage = texPage,
                TextureMode = texMode,
                IsDithered = isDithered,
                TransparencyMode = transMode,
            };
        }       

        public void ReadBackTexture(int x, int y, int width, int height, ref ushort[] texData) {
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, VramFrameBuffer);
            GL.ReadPixels(x, y, width, height, PixelFormat.Rgba, PixelType.UnsignedShort1555Reversed, texData);
        }

        public void VramFillRectangle(ref GPU_MemoryTransfer transfare) {
            RenderBatch();

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

            ReadOnlySpan<short> rectangle = [
                (short)x, (short)y,
                (short)(x+width), (short)y,
                (short)(x+width),(short)(y+height),
                (short)x, (short)(y+height)
             ];

            UpdateIntersectionTable(rectangle);
                        
            GL.Scissor(ScissorBox_X, ScissorBox_Y, ScissorBoxWidth, ScissorBoxHeight);
            GL.ClearColor(0, 0, 0, 1.0f);
            FrameUpdated = true;
        }

        public void CpuToVramCopy(ref GPU_MemoryTransfer transfare) {
            RenderBatch();

            int width = (int)transfare.Width;
            int height = (int)transfare.Height;

            int x_dst = (int)(transfare.Parameters[1] & 0x3FF);
            int y_dst = (int)((transfare.Parameters[1] >> 16) & 0x1FF);

            bool forceSetMaskBit = ((MaskBitSetting & 1) != 0);
            bool preserveMaskedPixels = (((MaskBitSetting >> 1) & 1) != 0);

            if (forceSetMaskBit) {
                for (int i = 0; i < transfare.Data.Length; i++) { transfare.Data[i] |= (1 << 15); }
            }

            /*//Slow
             ushort[] old = new ushort[width * height];
               if (preserveMaskedPixels) {
                 GL.ReadPixels(x_dst, y_dst, width, height, PixelFormat.Rgba, PixelType.UnsignedShort1555Reversed, old);
                 for (int i = 0; i < width * height; i++) {
                     if ((old[i] >> 15) == 1) {
                         transfare.Data[i] = old[i];
                     }
                 }
             }*/

            GL.Disable(EnableCap.ScissorTest);

            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, 0);
            GL.BindTexture(TextureTarget.Texture2D, VramTexture);
            GL.TexSubImage2D(TextureTarget.Texture2D, 0, x_dst, y_dst, width, height, 
                PixelFormat.Rgba, PixelType.UnsignedShort1555Reversed, transfare.Data);

            ReadOnlySpan<short> rectangle = [
                 (short)x_dst, (short)y_dst,
                (short)(x_dst+width), (short)y_dst,
                (short)(x_dst+width),(short)(y_dst+height),
                (short)x_dst, (short)(y_dst+height)
             ];

            UpdateIntersectionTable(rectangle);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, VramFrameBuffer);
            GL.Enable(EnableCap.ScissorTest);
            GL.Scissor(ScissorBox_X, ScissorBox_Y, ScissorBoxWidth, ScissorBoxHeight);
            FrameUpdated = true;
        }

        private void VramSync() {
            RenderBatch();

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
            RenderBatch();

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
            ReadOnlySpan<ushort> src_coords = [
                (ushort)x_src, (ushort)y_src,
                (ushort)(x_src + width), (ushort)y_src,
                (ushort)(x_src + width), (ushort)(y_src + height),
                (ushort)x_src, (ushort)(y_src + height)
            ];

            ReadOnlySpan<short> dst_coords = [
                (short)x_dst, (short)y_dst,
                (short)(x_dst + width), (short)y_dst,
                (short)(x_dst + width), (short)(y_dst + height),
                (short)x_dst, (short)(y_dst + height)
            ];

            //Make sure we sample from an up-to-date texture
            if (TextureInvalidate(src_coords)) {
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

            UpdateIntersectionTable(dst_coords);
            FrameUpdated = true;

        }

        public void VramToCpuCopy(ref GPU_MemoryTransfer transfare) {
            RenderBatch();

            int width = (int)transfare.Width;
            int height = (int)transfare.Height;

            int x_src = (int)(transfare.Parameters[1] & 0x3FF);
            int y_src = (int)((transfare.Parameters[1] >> 16) & 0x1FF);

            ReadBackTexture(x_src, y_src, width, height, ref transfare.Data);
        }

        public bool TextureInvalidatePrimitive(ReadOnlySpan<ushort> uv, uint texPage, uint clut) {
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

        public bool TextureInvalidate(ReadOnlySpan<ushort> coords) {       
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


        public void UpdateIntersectionTable(ReadOnlySpan<short> vertices) {
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
        private bool ApplyDrawingOffset(ref Span<short> vertices) {
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
            RenderBatch();
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

        void DisplayFrame() {
            //Disable scissoring and blending
            GL.Disable(EnableCap.ScissorTest);
            DisableBlending();

            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, VramFrameBuffer); //Bind VRAM framebuffer as a read framebuffer
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, 0);               //Bind screen frambuffer as draw framebuffer
            GL.BindTexture(TextureTarget.Texture2D, VramTexture);                   //Bind VRAM texture

            //Set render mode, view port, and aspect ratio
            RenderMode currentRenderMode = Is24bpp ? RenderMode.Rendering16bppAs24bppFullVram : RenderMode.Rendering16bppFullVram;
            GL.Uniform1(RenderModeLoc, (int)currentRenderMode);
            GL.Viewport(0, 0, this.Size.X, this.Size.Y);
            SetAspectRatio();

            //Draw
            GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);

            //Restore settings
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, VramFrameBuffer);     //Bind VRAM as Draw framebuffer
            GL.Enable(EnableCap.ScissorTest);                                           //Enable scissoring
            GL.BindTexture(TextureTarget.Texture2D, SampleTexture);                     //Bind VRAM sample texture
            GL.Scissor(ScissorBox_X, ScissorBox_Y, ScissorBoxWidth, ScissorBoxHeight);  //Set scissor box
            GL.Uniform1(RenderModeLoc, (int)RenderMode.RenderingPrimitives);            //Set render mode back to RenderingPrimitives

            EnableBlending();
        }

        public void DisableBlending() {
            GL.BlendFunc(BlendingFactor.One, BlendingFactor.Zero);
            GL.BlendEquation(BlendEquationMode.FuncAdd);
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
           // GL.DeleteBuffer(ColorsBuffer);
           // GL.DeleteBuffer(TexCoords);
            GL.DeleteVertexArray(VertexArrayObject);
            GL.DeleteFramebuffer(VramFrameBuffer);
            GL.DeleteTexture(VramTexture);
            GL.DeleteTexture(SampleTexture);
            GL.DeleteProgram(Shader.Program);
            base.OnUnload();
        }
    }

}
