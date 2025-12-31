using OpenTK.Graphics.OpenGL;
using PSXSharp.Peripherals.GPU;
using PSXSharp.Shaders;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace PSXSharp {
    public static partial class GLRenderBackend {
        //Shaders
        private static Shader Shader;
        public static int ShaderHandle => Shader.Program; //This must be used after calling Initialize

        //Locations
        private static int VertexArrayObject;
        private static int VertexBufferObject;
        private static int TexWindowLoc;
        private static int MaskBitSettingLoc;
        private static int RenderModeLoc;
        private static int IsCopy;

        //Primitive Settings
        private readonly static int SizeOfVertexInfo = Marshal.SizeOf<VertexInfo>();
        private readonly static int Stride = Marshal.SizeOf<VertexInfo>();
        private readonly static nint PositionOffset = Marshal.OffsetOf<VertexInfo>("Position");
        private readonly static nint ColorOffset = Marshal.OffsetOf<VertexInfo>("Color");
        private readonly static nint UVOffset = Marshal.OffsetOf<VertexInfo>("UV");
        private readonly static nint ClutOffset = Marshal.OffsetOf<VertexInfo>("Clut");
        private readonly static nint TexPageOffset = Marshal.OffsetOf<VertexInfo>("TexPage");
        private readonly static nint TexModeOffset = Marshal.OffsetOf<VertexInfo>("TextureMode");
        private readonly static nint DitherOffset = Marshal.OffsetOf<VertexInfo>("IsDithered");
        private readonly static nint TransOffset = Marshal.OffsetOf<VertexInfo>("TransparencyMode");

        //Global Settings
        public static int MaskBitSetting;
        private static int ScissorBox_X = 0;
        private static int ScissorBox_Y = 0;
        private static int ScissorBoxWidth = VRAM_WIDTH;
        private static int ScissorBoxHeight = VRAM_HEIGHT;
        private static ushort TexWindowX;
        private static ushort TexWindowY;
        private static ushort TexWindowZ;
        private static ushort TexWindowW;
        private static short DrawOffsetX = 0; //Signed 11 bits
        private static short DrawOffsetY = 0; //Signed 11 bits

        //Constants
        private const string VERTEX_SHADER_PATH = @"GLRenderer/Shaders/VertexShader.glsl";
        private const string FRAGMENT_SHADER_PATH = @"GLRenderer/Shaders/FragmentShader.glsl";

        private const int SCREEN_FRAMEBUFFER = 0;
        private const int VRAM_WIDTH = 1024;
        private const int VRAM_HEIGHT = 512;

        private const int VERTEX_ELEMENTS = 2;  //X, Y
        private const int UV_ELEMENTS = 2;      //U, V
        private const int COLOR_ELEMENTS = 3;   //R, G, B
        private const int REVERSE_SUBTRACT = 2; //B - F Transparency Mode

        public static bool FrameUpdated = false;
        
        public enum RenderMode {
            RenderingPrimitives = 0,                    //Normal mode that games will use to draw primitives
            Rendering16bppFullVram = 1,                 //When drawing the vram on screen
            Rendering16bppAs24bppFullVram = 2,          //When drawing the 16bpp vram as 24bpp
        }

        //Forward VRAM commands to the VramManager
        public static void ReadBackTexture(int x, int y, int width, int height, ref ushort[] texData) => VramManager.ReadBackTexture(x, y, width, height, ref texData);
        public static void CpuToVramCopy(ref GPU_MemoryTransfer transfare) => VramManager.CpuToVramCopy(ref transfare);
        public static void VramToVramCopy(ref GPU_MemoryTransfer transfare) => VramManager.VramToVramCopy(ref transfare);
        public static void VramToCpuCopy(ref GPU_MemoryTransfer transfare) => VramManager.VramToCpuCopy(ref transfare);
        public static void VramFillRectangle(ref GPU_MemoryTransfer transfare) => VramManager.VramFillRectangle(ref transfare);

        //Helpers
        private static short Signed11Bits(ushort input) => (short)(((short)(input << 5)) >> 5);
        private static bool ForceSetMaskBit => (MaskBitSetting & 1) != 0;
        private static bool PreserveMaskedPixels = ((MaskBitSetting >> 1) & 1) != 0;

        public static void Initialize() {
            string vertexShader = File.ReadAllText(VERTEX_SHADER_PATH);
            string fragmentShader = File.ReadAllText(FRAGMENT_SHADER_PATH);
            Shader = new Shader(vertexShader, fragmentShader);
            Shader.Use();

            //Get Locations
            TexWindowLoc = GL.GetUniformLocation(Shader.Program, "u_texWindow");
            IsCopy = GL.GetUniformLocation(Shader.Program, "isCopy");
            MaskBitSettingLoc = GL.GetUniformLocation(Shader.Program, "maskBitSetting");
            RenderModeLoc = GL.GetUniformLocation(Shader.Program, "renderMode");

            //Create VAO/VBO/Buffers and Textures
            VertexArrayObject = GL.GenVertexArray();
            VertexBufferObject = GL.GenBuffer();
            VramManager.Initialize();
            GL.BindVertexArray(VertexArrayObject);
        
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 2);
            GL.PixelStore(PixelStoreParameter.PackAlignment, 2);

            GL.Uniform1(GL.GetUniformLocation(Shader.Program, "u_vramTex"), 0);
            GL.Uniform1(RenderModeLoc, (int)RenderMode.RenderingPrimitives);

            SetupVertexAttributes();
            EnableBlending();
        }

        public static void PrepareToDisplayFrame(bool is24bpp) {
            //Flush the vertex buffer
            RenderBatcher.RenderBatch();

            //Disable scissoring and blending
            GL.Disable(EnableCap.ScissorTest);
            DisableBlending();
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, VramManager.VramFBOHandle);   //Bind VRAM framebuffer as a read framebuffer
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, SCREEN_FRAMEBUFFER);          //Bind screen frambuffer as draw framebuffer
            GL.BindTexture(TextureTarget.Texture2D, VramManager.VramTextureHandle);             //Bind VRAM texture

            RenderMode currentRenderMode = is24bpp ? RenderMode.Rendering16bppAs24bppFullVram : RenderMode.Rendering16bppFullVram;
            GL.Uniform1(RenderModeLoc, (int)currentRenderMode);
        }

        public static void RestoreSettings() {
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, VramManager.VramFBOHandle);     //Bind VRAM as Draw framebuffer
            GL.Enable(EnableCap.ScissorTest);                                           //Enable scissoring
            GL.BindTexture(TextureTarget.Texture2D, VramManager.SampleTextureHandle);                     //Bind VRAM sample texture
            GL.Scissor(ScissorBox_X, ScissorBox_Y, ScissorBoxWidth, ScissorBoxHeight);  //Set scissor box
            EnableBlending();
            GL.Uniform1(RenderModeLoc, (int)RenderMode.RenderingPrimitives);
        }

        public static void SetupVertexAttributes() {
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
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, VramManager.VramFBOHandle);
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
        }

        public static void EnableBlending() {
            //B = Destination / F = Source          
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.Src1Color, BlendingFactor.Src1Alpha);        //Alpha values are handled in GLSL
            GL.BlendEquation(BlendEquationMode.FuncAdd);
        }

        public static void DisableBlending() {
            GL.BlendFunc(BlendingFactor.One, BlendingFactor.Zero);
            GL.BlendEquation(BlendEquationMode.FuncAdd);
        }

        public static void BindVertexInfo() {
            GL.BindBuffer(BufferTarget.ArrayBuffer, VertexBufferObject);
            GL.BufferData(BufferTarget.ArrayBuffer, RenderBatcher.CurrentVertexIndex * SizeOfVertexInfo, RenderBatcher.VertexBuffer, BufferUsageHint.StreamDraw);
        }

        //This function can handle more than one triangle
        public static void DrawTrinangles(Span<short> vertices, ReadOnlySpan<byte> colors, ReadOnlySpan<ushort> uv,
            bool isTextured, ushort clut, ushort texPage, int textureMode, bool isDithered, int transMode) {                 
            if (!ApplyDrawingOffset(ref vertices)) { return; }

            RenderBatcher.EnsureEnoughBufferSpace(vertices.Length / VERTEX_ELEMENTS);
            RenderBatcher.SetBatchType(PrimitiveType.Triangles);

            //Sync vram if texture is dirty or if it's using B - F transparency mode
            bool needSync = (isTextured && VramManager.TextureInvalidatePrimitive(uv, texPage, clut)) || transMode == REVERSE_SUBTRACT;
            if (needSync) {
                VramManager.VramSync();
            }

            int ditheringValue = isDithered ? 1 : 0;
            int elementIndex = 0;          //Position and UV
            int colorIndex = 0;            //Color

            for (; elementIndex < vertices.Length; elementIndex += VERTEX_ELEMENTS, colorIndex += COLOR_ELEMENTS) {
                ReadOnlySpan<short> currentPosition = vertices.Slice(elementIndex, VERTEX_ELEMENTS);
                ReadOnlySpan<ushort> currentUV = uv.Slice(elementIndex, UV_ELEMENTS);
                ReadOnlySpan<byte> currentColor = colors.Slice(colorIndex, COLOR_ELEMENTS);
                RenderBatcher.AddVertex(currentPosition, currentColor, currentUV, clut, texPage, textureMode, ditheringValue, transMode);
            }

            VramManager.UpdateIntersectionTable(vertices);
            FrameUpdated = true;
        }

        public static void DrawLines(Span<short> vertices, ReadOnlySpan<byte> colors, bool isPolyLine, bool isDithered, int transMode) {
            if (!ApplyDrawingOffset(ref vertices)) { return; }

            PrimitiveType commandLinesType = isPolyLine ? PrimitiveType.LineStrip : PrimitiveType.Lines;
            RenderBatcher.EnsureEnoughBufferSpace(vertices.Length / VERTEX_ELEMENTS);
            RenderBatcher.SetBatchType(commandLinesType);

            //Sync vram if it's using B - F transparency mode
            bool needSync = transMode == REVERSE_SUBTRACT;
            if (needSync) {
                VramManager.VramSync();
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
                RenderBatcher.AddVertex(vertex, color, uv, clut, texPage, textureMode, ditheringValue, transMode);
            }

            //Don't batch LineStrips because this will result in connecting all of the different strips
            if (commandLinesType == PrimitiveType.LineStrip) {
                RenderBatcher.RenderBatch();
            }

            VramManager.UpdateIntersectionTable(vertices);
            FrameUpdated = true;
        }

        //Applies Drawing offset and checks if final dimensions are valid (within range)
        private static bool ApplyDrawingOffset(ref Span<short> vertices) {
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

        //Global settings setters
        public static void SetOffset(short x, short y) {
            //Already sign extended
            //Draw offset is handled in ApplyDrawingOffset
            DrawOffsetX = x;
            DrawOffsetY = y;
        }

        public static void SetTextureWindow(ushort x, ushort y, ushort z, ushort w) {
            //Return if nothing was changed
            if (x == TexWindowX && y == TexWindowY && z == TexWindowZ && w == TexWindowW) { return; } 

            //Otherwise flush and set the new values
            RenderBatcher.RenderBatch();
            GL.Uniform4(TexWindowLoc, x, y, z, w);
            TexWindowX = x;
            TexWindowY = y;
            TexWindowZ = z;
            TexWindowW = w;
        }

        public static void SetScissorBox(int x, int y, int width, int height) {
            //Return if nothing was changed
            if (x == ScissorBox_X && y == ScissorBox_Y && width == ScissorBoxWidth && height == ScissorBoxHeight) { return; }

            //Otherwise flush and set the new values
            RenderBatcher.RenderBatch();
            GL.Viewport(0, 0, VRAM_WIDTH, VRAM_HEIGHT);

            ScissorBox_X = x;
            ScissorBox_Y = y;
            ScissorBoxWidth = Math.Max(width + 1, 0);
            ScissorBoxHeight = Math.Max(height + 1, 0);

            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, VramManager.VramFBOHandle);

            GL.Enable(EnableCap.ScissorTest);
            GL.Scissor(ScissorBox_X, ScissorBox_Y, ScissorBoxWidth, ScissorBoxHeight);
        }

        public static void SetMaskBitSetting(int setting) {
            //Return if nothing was changed
            if (setting == MaskBitSetting) { return; }

            //Otherwise flush and set the new values
            RenderBatcher.RenderBatch();
            GL.Uniform1(MaskBitSettingLoc, setting);
            MaskBitSetting = setting;
        }

        public static void Destroy() {
            // Unbind all the resources by binding the targets to 0/null.
            GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
            GL.BindTexture(TextureTarget.Texture2D, 0);
            GL.BindVertexArray(0);
            GL.UseProgram(0);

            // Delete all the resources.
            GL.DeleteBuffer(VertexBufferObject);
            GL.DeleteVertexArray(VertexArrayObject);
            VramManager.Destroy();

            GL.DeleteProgram(Shader.Program);
        }
    }
}
