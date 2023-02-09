﻿using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Common.Input;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp;
using System;
using System.Diagnostics;
using System.Threading;
using System.Collections.Generic;
using System.Linq.Expressions;
using static System.Net.Mime.MediaTypeNames;
using System.IO;

namespace PS1_Emulator {
    public class Renderer {

        public Window window;
        
        public Renderer() {

            var nativeWindowSettings = new NativeWindowSettings() {
                Size = new Vector2i(1024, 512),
                Title = "PS1 Emulator",
                // This is needed to run on macos
                Flags = ContextFlags.ForwardCompatible,
                APIVersion = Version.Parse("4.6.0"),
                WindowBorder = WindowBorder.Fixed,
                Location = new Vector2i((1980 - 1024) / 2, (1080 - 512) / 2)
            };
            var Gws = GameWindowSettings.Default;
            Gws.RenderFrequency = 60;
            Gws.UpdateFrequency = 60;

            var windowIcon = new WindowIcon(new OpenTK.Windowing.Common.Input.Image(300, 300, ImageToByteArray(@"C:\Users\Old Snake\Desktop\PS1\PSX logo.jpg")));
            nativeWindowSettings.Icon = windowIcon;
            
            window = new Window(Gws, nativeWindowSettings);
            window.Run();

        }

        public  byte[] ImageToByteArray(string Icon) {
            var image = (Image<Rgba32>)SixLabors.ImageSharp.Image.Load(Configuration.Default, Icon);

            var pixels = new byte[4 * image.Width * image.Height];
            image.CopyPixelDataTo(pixels);

            return pixels;
        }



    }



    public class Window : GameWindow {
        public bool biosReagonFound = false;

        CPU cpu;
        bool paused = false;
        const int CYCLES_PER_FRAME = 33868800 / 60;


        private int vertexArrayObject;
        private int vertexBufferObject;
        private int colorsBuffer;
        private int uniform_offset;
        private int FullVram;
        private int vram_texture;
        private int sample_texture;
        private int texCoords;
        private int texWindow;
        private int texModeLoc;
        private int clutLoc;
        private int texPageLoc;
        private int display_areay_X_Loc;
        private int display_areay_Y_Loc;
        private int fbo;

        public bool isUsingMouse = false;

        Shader shader;

        //Map my button indexes to the corrosponding bits in the PS1 controller
        public static Dictionary<int, int> buttons_Dictionary = new Dictionary<int, int>()
         {
           {0, 15},      //Square
           {1, 14},      //X
           {2, 13},      //Circle
           {3, 12},      //Triangle
           {4, 10},      //L1
           {5, 11},      //R1
           {6, 8},       //L2
           {7, 9},       //R2
           {8, 0},       //Select
           {9, 3},       //Start
           {10, 1},      //L3
           {11, 2},      //R3
           {15, 4},      //Pad up
           {16, 5},      //Pad right
           {17, 6},      //Pad down
           {18, 7},      //Pad Left

        };

        public Window(GameWindowSettings gameWindowSettings, NativeWindowSettings nativeWindowSettings)
             : base(gameWindowSettings, nativeWindowSettings) {

            GL.ClearColor(0.0f, 0.0f, 0.0f, 1.0f);
            GL.Clear(ClearBufferMask.ColorBufferBit);
         
            SwapBuffers();

            //A shitty way, and a hardcoded path 
            BIOS bios = new BIOS(@"C:\Users\Old Snake\Desktop\PS1\BIOS\PSX - SCPH1001.BIN");
            Interconnect i = new Interconnect(bios, this);
            cpu = new CPU(i);

        }
        protected override void OnLoad() {
            
            //Load shaders 
            shader = new Shader("C:\\Users\\Old Snake\\Desktop\\PS1\\shader.vert", "C:\\Users\\Old Snake\\Desktop\\PS1\\shader.frag");
            shader.Use();

            
            GL.ClearColor(0, 0, 0, 1);
            GL.Clear(ClearBufferMask.ColorBufferBit);

            uniform_offset = GL.GetUniformLocation(shader.Handle, "offset");
            FullVram = GL.GetUniformLocation(shader.Handle, "FullVram");
            texWindow = GL.GetUniformLocation(shader.Handle, "u_texWindow");
            texModeLoc = GL.GetUniformLocation(shader.Handle, "TextureMode");
            clutLoc = GL.GetUniformLocation(shader.Handle, "inClut");
            texPageLoc = GL.GetUniformLocation(shader.Handle, "inTexpage");

            display_areay_X_Loc = GL.GetUniformLocation(shader.Handle, "display_area_x");
            display_areay_Y_Loc = GL.GetUniformLocation(shader.Handle, "display_area_y");

            vertexArrayObject = GL.GenVertexArray();
            vertexBufferObject = GL.GenBuffer();                 
            colorsBuffer = GL.GenBuffer();
            texCoords = GL.GenBuffer();

            GL.BindVertexArray(vertexArrayObject);


            vram_texture = GL.GenTexture();
            sample_texture = GL.GenTexture();

            GL.Enable(EnableCap.Texture2D);

            GL.BindTexture(TextureTarget.Texture2D, vram_texture);

            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);

            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, 1024, 512, 0, PixelFormat.Bgra, PixelType.UnsignedShort1555Reversed, (IntPtr)null);


            GL.BindTexture(TextureTarget.Texture2D, sample_texture);


            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, 1024, 512, 0, PixelFormat.Bgra, PixelType.UnsignedShort1555Reversed, (IntPtr)null);



            fbo = GL.GenFramebuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            GL.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, vram_texture, 0);

            if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != FramebufferErrorCode.FramebufferComplete) {

                Debug.WriteLine("Uncompleted Frame Buffer !");

            }


            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 2);
            GL.PixelStore(PixelStoreParameter.PackAlignment, 2);


            GL.Uniform1(GL.GetUniformLocation(shader.Handle, "u_vramTex"), 0);

        }

        public void setOffset(Int16 x, Int16 y, Int16 z) {

            GL.Uniform3(uniform_offset, x, y, z);

        }
        public void sewTextureWindow(ushort x, ushort y, ushort z, ushort w) {

            GL.Uniform4(texWindow, x, y, z, w);
        }

        int scissorBox_x;
        int scissorBox_y;
        int scissorBox_w;
        int scissorBox_h;

        public void ScissorBox(int x,int y,int width,int height) {
            scissorBox_x = x;
            scissorBox_y = y;
            scissorBox_w = width;
            scissorBox_h = height;

            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, fbo);
           // GL.Enable(EnableCap.ScissorTest);
            GL.Scissor(scissorBox_x, scissorBox_y, scissorBox_w, scissorBox_h);


        }

        public void draw(ref short[] vertices, ref byte[] colors, ref ushort[] uv, ushort clut, ushort page, int texMode) {
           
            GL.Uniform1(texModeLoc, texMode);

           // GL.Enable(EnableCap.ScissorTest);

            GL.BindBuffer(BufferTarget.ArrayBuffer, vertexBufferObject);
            GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(short), vertices, BufferUsageHint.StreamDraw);
            GL.VertexAttribIPointer(0, 3, VertexAttribIntegerType.Short, 0, (IntPtr)null);
            GL.EnableVertexAttribArray(0);

            GL.BindBuffer(BufferTarget.ArrayBuffer, colorsBuffer);
            GL.BufferData(BufferTarget.ArrayBuffer, colors.Length * sizeof(byte), colors, BufferUsageHint.StreamDraw);
            GL.VertexAttribIPointer(1, 3, VertexAttribIntegerType.UnsignedByte, 0, (IntPtr)null);
            GL.EnableVertexAttribArray(1);
            
            if (uv != null) {
                GL.Uniform1(clutLoc, clut);
                GL.Uniform1(texPageLoc, page);

                GL.BindBuffer(BufferTarget.ArrayBuffer, texCoords);
                GL.BufferData(BufferTarget.ArrayBuffer, uv.Length * sizeof(ushort), uv, BufferUsageHint.StreamDraw);
                GL.VertexAttribPointer(2, 2, VertexAttribPointerType.UnsignedShort, false, 2 * sizeof(ushort), (IntPtr)null);
                GL.EnableVertexAttribArray(2);
            }

            GL.DrawArrays(PrimitiveType.Triangles, 0, vertices.Length / 3);

            //Lets hope there is no need to sync and wait for the GPU 

        }

        public void blendingMode(uint page) { 
            GL.Enable(EnableCap.Blend);

            uint mode = (page >> 5) & 3;
            GL.BlendFuncSeparate(BlendingFactorSrc.Src1Color,BlendingFactorDest.Src1Alpha,BlendingFactorSrc.One,BlendingFactorDest.Zero);

            switch (mode) {
                case 0:
                    GL.BlendEquation(BlendEquationMode.FuncAdd);
                    setBlendFactors(0.5f, 0.5f);

                    break;
                case 1:
                    GL.BlendEquation(BlendEquationMode.FuncAdd);
                    setBlendFactors(1f, 1f);

                    break;
                case 2:
                    GL.BlendEquationSeparate(BlendEquationMode.FuncReverseSubtract, BlendEquationMode.FuncAdd);
                    setBlendFactors(1f, 1f);

                    break;
                case 3:
                    GL.BlendEquation(BlendEquationMode.FuncAdd);
                    setBlendFactors(1f, 0.25f);

                    break;

            }

        }

        public void setBlendFactors(float des,float src) {

           GL.Uniform4(GL.GetUniformLocation(shader.Handle, "u_blendFactors"), des, des, des, src);

        }
        public void readBackTexture(UInt16 x, UInt16 y, UInt16 width, UInt16 height, ref UInt16[] texData) {

            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, fbo);
            GL.ReadPixels(x, y, width, height, PixelFormat.Rgba, PixelType.UnsignedShort1555Reversed, texData);

        }

        void displayVramContent() {
            //Disable the ScissorTest and unbind the FBO to draw the entire vram texture to the screen
            GL.Disable(EnableCap.ScissorTest);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, 0);

            GL.Disable(EnableCap.Blend);
            GL.Viewport(0, 0, 1024, 512);
            GL.Enable(EnableCap.Texture2D);
            GL.BindTexture(TextureTarget.Texture2D, vram_texture);

            GL.Uniform1(FullVram, 1);

            GL.DisableVertexAttribArray(1);
            GL.DisableVertexAttribArray(2);

            GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);

            //Enable ScissorTest and bind FBO for next draws 
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, fbo);
            GL.BindTexture(TextureTarget.Texture2D, sample_texture);
            GL.Enable(EnableCap.ScissorTest);

            GL.Uniform1(FullVram, 0);

            //GL.Uniform1(display_areay_X_Loc, (float)scissorBox_w); //Display area needs more work
            //GL.Uniform1(display_areay_Y_Loc, (float)scissorBox_h);

        }
        public void fill(float r,float g,float b, int x, int y, int width, int height) {

            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, fbo);
            GL.ClearColor(r, g, b, 1.0f);       //Fill by clear needs Scissor box
            GL.Scissor(x,y,width,height);
            GL.Clear(ClearBufferMask.ColorBufferBit);

            //After that reset the Scissor box to the drawing area
            GL.Scissor(scissorBox_x, scissorBox_y, scissorBox_w, scissorBox_h);

        }



        public void update_vram(int x, int y , int width, int height, ushort[] textureData) {
            if (width == 0) { width = 1024; }
            if (height == 0) { height = 512; }
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, 0);
            GL.BindTexture(TextureTarget.Texture2D, vram_texture);
            GL.TexSubImage2D(TextureTarget.Texture2D,0,x,y,width,height, PixelFormat.Rgba, PixelType.UnsignedShort1555Reversed, textureData);
            
            GL.BindTexture(TextureTarget.Texture2D, sample_texture);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, fbo);
            GL.CopyTexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, 0, 0, 1024, 512);

         
        }
        internal void VramToVramCopy(int x0_src, int y0_src, int x0_dest, int y0_dest, int width, int height) {
            //WIP

            /*GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
            //GL.BindTexture(TextureTarget.Texture2D, sample_texture);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, fbo);

            GL.BlitFramebuffer(x0_src,y0_src,x1_src,y1_src,x0_dest,y0_dest,x1_dest,y1_dest, (ClearBufferMask)ClearBuffer.Color,BlitFramebufferFilter.Nearest);*/
            if(width == 0) { width = 1024; }
            if(height == 0) { height = 512; }
            GL.BindTexture(TextureTarget.Texture2D, sample_texture);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, 0); 
            GL.CopyImageSubData(sample_texture,ImageTarget.Texture2D,0,x0_src,y0_src,0,vram_texture,ImageTarget.Texture2D,0,x0_dest,y0_dest,0,width,height,0);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, fbo);
            GL.CopyTexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, 0, 0, 1024, 512);

            //throw new Exception("VramToVramCopy");
        }

        public void display() {
            
            displayVramContent();
            SwapBuffers();

        }

     
        protected override void OnRenderFrame(FrameEventArgs e) {
            base.OnRenderFrame(e);

            for (int i=0; i < CYCLES_PER_FRAME; i++) {        //Timings are nowhere near accurate 
                if (!paused) {

                    /*try {
                        cpu.emu_cycle();

                    }catch(Exception ex) {
                         File.WriteAllTextAsync("Crash.log", ex.ToString());
                         Close();
                    }*/
                    cpu.emu_cycle();


                    CPU.incrementSynchronizer();
                    CPU.incrementSynchronizer();

                    cpu.bus.TIMER2_tick(CPU.sync);

                    cpu.SPUtick();      

                    cpu.GPUtick();
                   //cpu.GPUtick();

                    cpu.IOtick();
                    cpu.IOtick();

                    cpu.CDROMtick();
                   
                    CPU.sync = 0;

                }

            }


            cpu.bus.IO_PORTS.controller.isConnected = JoystickStates[0] != null;

            if (cpu.bus.IO_PORTS.controller.isConnected) {
                for (int j = 0; j < JoystickStates[0].ButtonCount; j++) {
                    if (buttons_Dictionary.ContainsKey(j)) {
                        if (JoystickStates[0].IsButtonDown(j)) {
                            int bit = ~(1 << buttons_Dictionary[j]);
                            cpu.bus.IO_PORTS.controller.buttons &= (ushort)(bit);

                        }
                        else {
                            int bit = (1 << buttons_Dictionary[j]);
                            cpu.bus.IO_PORTS.controller.buttons |= (ushort)(bit);
                        }

                    }

                }
            }

            if (KeyboardState.IsKeyDown(Keys.Escape)) {
                Close();
            }
            else if (KeyboardState.IsKeyDown(Keys.D)) {
                cpu.bus.print = true;
                Thread.Sleep(100);

            }
            else if (KeyboardState.IsKeyDown(Keys.P)) {
                paused = !paused;
                Thread.Sleep(100);

            }


        }
        

        protected override void OnUnload() {

            // Unbind all the resources by binding the targets to 0/null.
            // Unbind all the resources by binding the targets to 0/null.
            GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
            GL.BindTexture(TextureTarget.Texture2D, 0);
            GL.BindVertexArray(0);
            GL.UseProgram(0);

            // Delete all the resources.
            GL.DeleteBuffer(vertexBufferObject);
            GL.DeleteBuffer(colorsBuffer);
            GL.DeleteBuffer(texCoords);
            GL.DeleteVertexArray(vertexArrayObject);
            GL.DeleteFramebuffer(fbo);
            GL.DeleteTexture(vram_texture);
            GL.DeleteTexture(sample_texture);
            GL.DeleteProgram(shader.Handle);

            
            base.OnUnload();
        }

       
    }




}
