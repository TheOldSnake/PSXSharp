using OpenTK.Graphics.OpenGL;
using PSXSharp.Peripherals.GPU;
using System;

namespace PSXSharp {
    public partial class GLRenderBackend {
        private static class VramManager {
            private static int VramTexture;
            private static int VramFrameBuffer;
            private static int SampleTexture;

            public static int VramTextureHandle => VramTexture;
            public static int VramFBOHandle => VramTexture;
            public static int SampleTextureHandle => VramTexture;

            //Texture invalidation 
            private const int IntersectionBlockLength = 64;
            private static readonly int[,] IntersectionTable = new int[VRAM_HEIGHT / IntersectionBlockLength, VRAM_WIDTH / IntersectionBlockLength];

            public static void Initialize() {
                VramTexture = GL.GenTexture();
                SampleTexture = GL.GenTexture();
                VramFrameBuffer = GL.GenFramebuffer();

                GL.Enable(EnableCap.Texture2D);
                SetupTexture(VramTexture);
                SetupTexture(SampleTexture);

                GL.BindTexture(TextureTarget.Texture2D, SampleTexture);
                GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, VramFrameBuffer);
                GL.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, VramTexture, 0);

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
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgb5A1, VRAM_WIDTH, VRAM_HEIGHT, 0, PixelFormat.Rgba, PixelType.UnsignedShort1555Reversed, (IntPtr)null);
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
           
            public static void ReadBackTexture(int x, int y, int width, int height, ref ushort[] texData) {
                GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, VramFBOHandle);
                GL.ReadPixels(x, y, width, height, PixelFormat.Rgba, PixelType.UnsignedShort1555Reversed, texData);
            }

            public static void VramToCpuCopy(ref GPU_MemoryTransfer transfare) {
                RenderBatcher.RenderBatch();

                int width = (int)transfare.Width;
                int height = (int)transfare.Height;

                int x_src = (int)(transfare.Parameters[1] & 0x3FF);
                int y_src = (int)((transfare.Parameters[1] >> 16) & 0x1FF);

                ReadBackTexture(x_src, y_src, width, height, ref transfare.Data);
            }

            public static void CpuToVramCopy(ref GPU_MemoryTransfer transfare) {
                RenderBatcher.RenderBatch();

                int width = (int)transfare.Width;
                int height = (int)transfare.Height;

                int x_dst = (int)(transfare.Parameters[1] & 0x3FF);
                int y_dst = (int)((transfare.Parameters[1] >> 16) & 0x1FF);

                if (ForceSetMaskBit) {
                    const ushort MASK_BIT = 1 << 15;
                    for (int i = 0; i < transfare.Data.Length; i++) { transfare.Data[i] |= MASK_BIT; }
                }

                /*//Slow
                 ushort[] old = new ushort[width * height];
                   if (PreserveMaskedPixels) {
                     GL.ReadPixels(x_dst, y_dst, width, height, PixelFormat.Rgba, PixelType.UnsignedShort1555Reversed, old);
                     for (int i = 0; i < width * height; i++) {
                         if ((old[i] >> 15) == 1) {
                             transfare.Data[i] = old[i];
                         }
                     }
                 }*/

                GL.Disable(EnableCap.ScissorTest);                                            //Disable scissoring
                GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, SCREEN_FRAMEBUFFER);    //Unbind vram FBO
                GL.BindTexture(TextureTarget.Texture2D, VramTextureHandle);                   //Bind vram as texture
                GL.TexSubImage2D(TextureTarget.Texture2D, 0, x_dst, y_dst, width, height,     //Upload data to the vram texture
                    PixelFormat.Rgba, PixelType.UnsignedShort1555Reversed, transfare.Data);
                
                //Make sure to mark the affected area as dirty
                Span<short> rectangleCoords = stackalloc short[VERTEX_ELEMENTS * 4];
                Rectangle.WriteRectangleCoords(x_dst, y_dst, width, height, rectangleCoords);
                UpdateIntersectionTable(rectangleCoords);

                //Rebind the vram FBO as draw, and reenable scissoring
                GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, VramFBOHandle);      
                GL.Enable(EnableCap.ScissorTest);
                GL.Scissor(ScissorBox_X, ScissorBox_Y, ScissorBoxWidth, ScissorBoxHeight);
                FrameUpdated = true;
            }

            public static void VramToVramCopy(ref GPU_MemoryTransfer transfare) {
                RenderBatcher.RenderBatch();

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
                Span<ushort> src_coords = stackalloc ushort[VERTEX_ELEMENTS * 4];
                Span<short> dst_coords = stackalloc short[VERTEX_ELEMENTS * 4];
                Rectangle.WriteRectangleCoords(x_src, y_src, width, height, src_coords);
                Rectangle.WriteRectangleCoords(x_dst, y_dst, width, height, dst_coords);

                //Make sure we sample from an up-to-date texture
                if (TextureInvalidate(src_coords)) {
                    VramSync();
                }

                //Bind GL stuff
                GL.BindTexture(TextureTarget.Texture2D, SampleTextureHandle);
                GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, VramFBOHandle);

                GL.CopyImageSubData(
                    SampleTextureHandle, ImageTarget.Texture2D, 0, x_src, y_src, 0,
                    VramTextureHandle, ImageTarget.Texture2D, 0, x_dst, y_dst, 0,
                    width, height, 1
                );

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
                GL.DeleteTexture(VramTexture);
                GL.DeleteTexture(SampleTexture);
            }
        }
    }
}
