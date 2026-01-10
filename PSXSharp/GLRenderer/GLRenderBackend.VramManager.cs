using OpenTK.Graphics.OpenGL;
using PSXSharp.Peripherals.GPU;
using System;

namespace PSXSharp {
    public partial class GLRenderBackend {
        private static class VramManager {
            private static int VramBaseTexture;
            private static int VramReadView;
            private static int VramWriteView;
            private static int VramFrameBuffer;
            private static int SampleTexture;
            private static int TempTexture;

            //Compute shader uniform locations
            private static int TransfereSrcRect_Loc;
            private static int TransfereDstRect_Loc;
            private static int TransfereDimensions_Loc;
            private static int TempTexture_Loc;    
            private static int TransferType_Loc;    
            public static int MaskBitSetting_Transfer_Loc;    

            public static int VramTextureHandle => VramBaseTexture;
            public static int VramFBOHandle => VramFrameBuffer;
            public static int SampleTextureHandle => SampleTexture;

            //Texture invalidation 
            private const int IntersectionBlockLength = 64;
            private static readonly int[,] IntersectionTable = new int[VRAM_HEIGHT / IntersectionBlockLength, VRAM_WIDTH / IntersectionBlockLength];

            //Transfer type constants
            private enum TransferType {
                TRANSFER_CPU_VRAM = 0,
                TRANSFER_VRAM_VRAM = 1,
            }

            public static void Initialize() {
                VramBaseTexture = GL.GenTexture();
                VramReadView = GL.GenTexture();
                VramWriteView = GL.GenTexture();
                SampleTexture = GL.GenTexture();
                TempTexture = GL.GenTexture();

                VramFrameBuffer = GL.GenFramebuffer();

                TransfereSrcRect_Loc = GL.GetUniformLocation(TransferShaderHandle, "srcRect");
                TransfereDstRect_Loc = GL.GetUniformLocation(TransferShaderHandle, "dstRect");
                TempTexture_Loc = GL.GetUniformLocation(TransferShaderHandle, "tempTex");
                TransferType_Loc = GL.GetUniformLocation(TransferShaderHandle, "transferType");
                TransfereDimensions_Loc = GL.GetUniformLocation(TransferShaderHandle, "dimensions");
                MaskBitSetting_Transfer_Loc = GL.GetUniformLocation(TransferShaderHandle, "maskBitSetting"); 

                GL.Enable(EnableCap.Texture2D);
                SetupTexture(VramBaseTexture);
                SetupTexture(SampleTexture);
                SetupTexture(TempTexture);

                //Set the vram read/write views for the compute shader
                CreateVramView(VramReadView);
                CreateVramView(VramWriteView);

                GL.BindTexture(TextureTarget.Texture2D, SampleTexture);
                GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, VramFrameBuffer);
                GL.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, VramBaseTexture, 0);

                if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != FramebufferErrorCode.FramebufferComplete) {
                    throw new Exception("[OpenGL] Uncompleted Frame Buffer !");
                }
            }

            private static void SetupTexture(int handle) {
                GL.BindTexture(TextureTarget.Texture2D, handle);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
                GL.TexStorage2D(TextureTarget2d.Texture2D, 1, SizedInternalFormat.Rgba8, VRAM_WIDTH, VRAM_HEIGHT);
            }

            private static void CreateVramView(int textureHandle) {
                GL.TextureView(textureHandle, TextureTarget.Texture2D, VramBaseTexture, PixelInternalFormat.Rgba8, 0, 1, 0, 1);
            }

            public static void VramSync() {
                RenderBatcher.RenderBatch();

                GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, VramFBOHandle);
                GL.BindTexture(TextureTarget.Texture2D, SampleTextureHandle);
                GL.CopyTexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, 0, 0, VRAM_WIDTH, VRAM_HEIGHT);
                GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, VramFBOHandle);

                //Reset all blocks to clean
                for (int i = 0; i < VRAM_WIDTH / IntersectionBlockLength; i++) {
                    for (int j = 0; j < VRAM_HEIGHT / IntersectionBlockLength; j++) {
                        IntersectionTable[j, i] = 0;
                    }
                }
            }

            private static void SwitchToTransferShader() {
                GL.UseProgram(TransferShaderHandle);                   //Switch to the compute shader to perform the transfer
                GL.BindTextureUnit(0, TempTexture);                    //Bind tempTex to texture 0 
                GL.BindTextureUnit(1, VramReadView);                   //Bind VramReadView to texture 1
                GL.BindImageTexture(2, VramWriteView, 0, false, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba8);   //Bind VramWriteView to texture 2
            }

            private static void WriteTransferUniforms(int src_x, int src_y, int dst_x, int dst_y, int width, int height, TransferType transferType) {
                GL.Uniform2(TransfereSrcRect_Loc, src_x, src_y);
                GL.Uniform2(TransfereDstRect_Loc, dst_x, dst_y);
                GL.Uniform2(TransfereDimensions_Loc, width, height);
                GL.Uniform1(TransferType_Loc, (int)transferType);
            }

            private static void DispatchTransferShader(int width, int height) {
                //Dispatch compute shader (16x16 threads per group)
                const int WORKGROUP_WIDTH = 16;
                const int WORKGROUP_HEIGHT = 16;
                int groupX = (int)Math.Ceiling((float)width / WORKGROUP_WIDTH);
                int groupY = (int)Math.Ceiling((float)height / WORKGROUP_HEIGHT);
                GL.DispatchCompute(groupX, groupY, 1);

                //Make sure writes are visible for next rendering commands
                GL.MemoryBarrier(MemoryBarrierFlags.TextureFetchBarrierBit | MemoryBarrierFlags.ShaderImageAccessBarrierBit);
            }

            private static void HandleTransfer(int src_x, int src_y, int dst_x, int dst_y, int width, int height, TransferType transferType, ushort[]? data = null) {
                //Console.WriteLine($"Src ({src_x}, {src_y}) Dst ({dst_x}, {dst_y}) Size ({width}x{height})");
                GL.Disable(EnableCap.ScissorTest);

                if (transferType == TransferType.TRANSFER_CPU_VRAM) {
                    //Upload data to the temporary texture (still using the regular program)
                    GL.BindTexture(TextureTarget.Texture2D, TempTexture);
                    GL.TexSubImage2D(TextureTarget.Texture2D, 0, src_x, src_y, width, height, PixelFormat.Rgba, PixelType.UnsignedShort1555Reversed, data);
                }

                SwitchToTransferShader();
                WriteTransferUniforms(src_x, src_y, dst_x, dst_y, width, height, transferType);
                DispatchTransferShader(width, height);

                //Switch back to the main shader
                GL.UseProgram(MainShaderHandle);
                GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, VramFBOHandle);
                GL.BindTexture(TextureTarget.Texture2D, SampleTexture);
                GL.Enable(EnableCap.ScissorTest);
                GL.Scissor(ScissorBox_X, ScissorBox_Y, ScissorBoxWidth, ScissorBoxHeight);
                GL.Uniform1(RenderModeLoc, (int)RenderMode.RenderingPrimitives);
            }

            public static void VramToCpuCopy(ref GPU_MemoryTransfer transfare) {
                RenderBatcher.RenderBatch();

                int width = (int)transfare.Width;
                int height = (int)transfare.Height;

                int x_src = (int)(transfare.Parameters[1] & 0x3FF);
                int y_src = (int)((transfare.Parameters[1] >> 16) & 0x1FF);

                GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, VramFBOHandle);
                GL.ReadPixels(x_src, y_src, width, height, PixelFormat.Rgba, PixelType.UnsignedShort1555Reversed, transfare.Data);
                GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, VramFBOHandle);
            }

            public static void CpuToVramCopy(ref GPU_MemoryTransfer transfare) {
                RenderBatcher.RenderBatch();

                int width = (int)transfare.Width;
                int height = (int)transfare.Height;

                int x_dst = (int)(transfare.Parameters[1] & 0x3FF);
                int y_dst = (int)((transfare.Parameters[1] >> 16) & 0x1FF);
               
                HandleTransfer(0, 0, x_dst, y_dst, width, height, TransferType.TRANSFER_CPU_VRAM, transfare.Data);

                //Make sure to mark the affected area as dirty
                Span<short> dst_coords = stackalloc short[VERTEX_ELEMENTS * 4];
                Rectangle.WriteRectangleCoords(x_dst, y_dst, width, height, dst_coords);
                UpdateIntersectionTable(dst_coords);
                FrameUpdated = true;
            }
         
            public static void VramToVramCopy(ref GPU_MemoryTransfer transfare) {
                RenderBatcher.RenderBatch();

                int width = (int)transfare.Width;
                int height = (int)transfare.Height;

                int x_src = (int)(transfare.Parameters[1] & 0x3FF);
                int x_dst = (int)(transfare.Parameters[2] & 0x3FF);

                int y_src = (int)((transfare.Parameters[1] >> 16) & 0x1FF);
                int y_dst = (int)((transfare.Parameters[2] >> 16) & 0x1FF);

                HandleTransfer(x_src, y_src, x_dst, y_dst, width, height, TransferType.TRANSFER_VRAM_VRAM);

                //Make sure to mark the affected area as dirty
                Span<short> dst_coords = stackalloc short[VERTEX_ELEMENTS * 4];
                Rectangle.WriteRectangleCoords(x_dst, y_dst, width, height, dst_coords);
                UpdateIntersectionTable(dst_coords);
                FrameUpdated = true;
            }

            public static void VramFillRectangle(ref GPU_MemoryTransfer transfare) {
                RenderBatcher.RenderBatch();

                int width = (int)transfare.Width;
                int height = (int)transfare.Height;

                int x = (int)(transfare.Parameters[1] & 0x3F0);
                int y = (int)((transfare.Parameters[1] >> 16) & 0x1FF);

                float r = (transfare.Parameters[0] & 0xFF) / 255.0f;
                float g = ((transfare.Parameters[0] >> 8) & 0xFF) / 255.0f;
                float b = ((transfare.Parameters[0] >> 16) & 0xFF) / 255.0f;

                //Mask bit setting does NOT affect the Fill-VRAM command, so we can simply use GL.Clear.

                GL.Viewport(0, 0, VRAM_WIDTH, VRAM_HEIGHT);
                GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, VramFBOHandle);
                GL.ClearColor(r, g, b, 0.0f);       //alpha = 0 (bit 15)
                GL.Scissor(x, y, width, height);
                GL.Clear(ClearBufferMask.ColorBufferBit);

                Span<short> rectangleCoords = stackalloc short[VERTEX_ELEMENTS * 4];
                Rectangle.WriteRectangleCoords(x, y, width, height, rectangleCoords);
                UpdateIntersectionTable(rectangleCoords);

                GL.Scissor(ScissorBox_X, ScissorBox_Y, ScissorBoxWidth, ScissorBoxHeight);
                GL.ClearColor(0, 0, 0, 1.0f);
                FrameUpdated = true;
            }

            public static bool TextureInvalidatePrimitive(ReadOnlySpan<ushort> uv, uint texPage, uint clut) {
                //Experimental 
                //Checks whether the textured primitive is reading from a dirty block

                //Hack: Always sync if preserve_masked_pixels is true
                //This is kind of slow but fixes Silent Hills 
                if (PreserveMaskedPixels) {
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

                uint left = texBaseX / IntersectionBlockLength;
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

            public static bool TextureInvalidate(ReadOnlySpan<ushort> coords) {
                //Hack: Always sync if preserve_masked_pixels is true
                //This is kind of slow but fixes Silent Hills 
                if (PreserveMaskedPixels) {
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

            public static void UpdateIntersectionTable(ReadOnlySpan<short> vertices) {
                //Mark any affected blocks as dirty

                const int VRAM_START_X  = 0;
                const int VRAM_END_X    = 1023;
                const int VRAM_START_Y  = 0;
                const int VRAM_END_Y    = 511;

                int minX, maxX, minY, maxY;
                minX = maxX = vertices[0];
                minY = maxY = vertices[1];

                for (int i = 0; i < vertices.Length; i += 2) {
                    int x = vertices[i];
                    int y = vertices[i + 1];
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                }

                minX = Math.Clamp(minX, VRAM_START_X, VRAM_END_X);
                maxX = Math.Clamp(maxX, VRAM_START_X, VRAM_END_X);
                minY = Math.Clamp(minY, VRAM_START_Y, VRAM_END_Y);
                maxY = Math.Clamp(maxY, VRAM_START_Y, VRAM_END_Y);

                int bounding_x1 = minX / IntersectionBlockLength;
                int bounding_x2 = maxX / IntersectionBlockLength;
                int bounding_y1 = minY / IntersectionBlockLength;
                int bounding_y2 = maxY / IntersectionBlockLength;

                //No access wrap for drawing, anything out of bounds is clamped 
                for (int y = bounding_y1; y <= bounding_y2; y++) {
                    for (int x = bounding_x1; x <= bounding_x2; x++) {
                        IntersectionTable[y, x] = 1;
                    }
                }
            }

            public static void Destroy() {
                GL.DeleteFramebuffer(VramFrameBuffer);
                GL.DeleteTexture(VramBaseTexture);
                GL.DeleteTexture(VramWriteView);
                GL.DeleteTexture(VramReadView);
                GL.DeleteTexture(SampleTexture);
                GL.DeleteTexture(TempTexture);
            }
        }
    }
}
