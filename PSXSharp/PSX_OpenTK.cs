using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using PSXSharp.Core;
using PSXSharp.Peripherals.IO;
using PSXSharp.Peripherals.MDEC;
using PSXSharp.Peripherals.Timers;
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Timers;
using Keys = OpenTK.Windowing.GraphicsLibraryFramework.Keys;

namespace PSXSharp {
    public class PSX_OpenTK {
        public PSX_OpenTK(string? biosPath, string? bootPath, bool isBootingEXE) {
            //Disable CheckForMainThread to allow running from a secondary thread
            GLFWProvider.CheckForMainThread = false;
            
            var nativeWindowSettings = new NativeWindowSettings() {
                Size = new Vector2i(1024, 512),
                Flags = ContextFlags.ForwardCompatible,
                APIVersion = Version.Parse("4.6.0"),
                WindowBorder = WindowBorder.Resizable,
            };

            nativeWindowSettings.Location = AtCenterOfScreen(nativeWindowSettings.Size);

            var Gws = GameWindowSettings.Default;
            Gws.RenderFrequency = 00;   
            Gws.UpdateFrequency = 00;

            EmulatorWindow mainWindow = new EmulatorWindow(Gws, nativeWindowSettings);
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
            GPU Gpu = new GPU(ref Timer0, ref Timer1);

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

            string bootName;
            if (bootPath != null) {
                bootName = Path.GetFileName(bootPath);
            } else {
                bootName = "PSX-BIOS";
            }

            mainWindow.Title = $"OpenGL | {cpuType} | {bootName}";
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

    public class EmulatorWindow : GameWindow {
        public CPU MainCPU;
        public bool IsEmuPaused;

        private int Display_Area_X_Start_Loc;
        private int Display_Area_Y_Start_Loc;
        private int Display_Area_X_End_Loc;
        private int Display_Area_Y_End_Loc;

        private int Aspect_Ratio_X_Offset_Loc;
        private int Aspect_Ratio_Y_Offset_Loc;

        //General stuff
        public bool IsUsingMouse = false;
        public bool ShowTextures = true;
        public bool IsFullScreen = false;

        private const int VRAM_WIDTH = 1024;
        private const int VRAM_HEIGHT = 512;
        public static bool Is24bpp = false;

        public System.Timers.Timer FrameTimer;
        public int Frames = 0;
        public string TitleCopy;

        private static EmulatorWindow Instance;
        public EmulatorWindow(GameWindowSettings gameWindowSettings, NativeWindowSettings nativeWindowSettings)
             : base(gameWindowSettings, nativeWindowSettings) {
            if (Instance != null) {
                throw new Exception("You cannot create more than one instance of EmulatorWindow");
            }

            Instance = this;

            //Initialize the renderer
            GLRenderBackend.Initialize();

            //Clear the window
            GL.ClearColor(0.0f, 0.0f, 0.0f, 1.0f);
            GL.Clear(ClearBufferMask.ColorBufferBit);
            SwapBuffers();
            SetTimer();
        }

        protected override void OnLoad() {
            GL.Viewport(0, 0, this.Size.X, this.Size.Y);
            GL.ClearColor(0.0f, 0.0f, 0.0f, 1.0f);      //This can be ignored as the PS1 BIOS will initially draw a black quad clearing the buffer anyway
            GL.Clear(ClearBufferMask.ColorBufferBit);  
            SwapBuffers();

            Display_Area_X_Start_Loc = GL.GetUniformLocation(GLRenderBackend.MainShaderHandle, "display_area_x_start");
            Display_Area_Y_Start_Loc = GL.GetUniformLocation(GLRenderBackend.MainShaderHandle, "display_area_y_start");
            Display_Area_X_End_Loc = GL.GetUniformLocation(GLRenderBackend.MainShaderHandle, "display_area_x_end");
            Display_Area_Y_End_Loc = GL.GetUniformLocation(GLRenderBackend.MainShaderHandle, "display_area_y_end");

            Aspect_Ratio_X_Offset_Loc = GL.GetUniformLocation(GLRenderBackend.MainShaderHandle, "aspect_ratio_x_offset");
            Aspect_Ratio_Y_Offset_Loc = GL.GetUniformLocation(GLRenderBackend.MainShaderHandle, "aspect_ratio_y_offset");

            if (JoystickStates[0] != null) {
                Console.WriteLine($"Controller Name: {JoystickStates[0].Name}");
            }
        }

        private void SetTimer() {
            // Create a timer with a 1 second interval.
            FrameTimer = new System.Timers.Timer(1000);

            // Hook up the Elapsed event for the timer. 
            FrameTimer.Elapsed += OnTimedEvent;
            FrameTimer.AutoReset = true;
            FrameTimer.Enabled = true;
        }

        public static void UpdateWindow() {
            Instance.DrawFrame();
            Instance.SwapBuffers();
            if (GLRenderBackend.FrameUpdated) {
                Instance.Frames++;
                GLRenderBackend.FrameUpdated = false;
            }     
        }

        private void DrawFrame() {
            GLRenderBackend.PrepareToDisplayFrame(Is24bpp);

            //Set view port, and aspect ratio 
            GL.Viewport(0, 0, this.Size.X, this.Size.Y);
            SetAspectRatio();

            //Draw (dummy verticies)
            GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);

            //Restore settings
            GLRenderBackend.RestoreSettings();
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

                    //GL.Scissor(ScissorBox_X, ScissorBox_Y, ScissorBoxWidth, ScissorBoxHeight);

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

                    //GL.Scissor(ScissorBox_X, ScissorBox_Y, ScissorBoxWidth, ScissorBoxHeight);

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

        protected override void OnUpdateFrame(FrameEventArgs args) {
            base.OnUpdateFrame(args);

            if (IsEmuPaused) { return; }

            //Clock the CPU
            MainCPU.TickFrame();

            //Read controller input 
            MainCPU.GetBUS().JOY_IO.Controller1.ReadInput(JoystickStates[0]);
        }

        private void OnTimedEvent(Object source, ElapsedEventArgs e) {
            double speed = MainCPU.GetSpeed();
            Title = $"{TitleCopy} | FPS {Frames} | CPU: {speed:00.00}%";
            Frames = 0;
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

            switch (e.Key) { 
                case Keys.Escape: Close(); break;
                case Keys.P: IsEmuPaused = !IsEmuPaused; break;
                case Keys.Tab: ShowTextures = !ShowTextures; break;

                case Keys.F:
                    IsFullScreen = !IsFullScreen;
                    WindowState = IsFullScreen ? WindowState.Fullscreen : WindowState.Normal;
                    CursorState = IsFullScreen ? CursorState.Hidden : CursorState.Normal;
                    break;

                case Keys.C:
                    MainCPU.GetBUS().JOY_IO.Controller1.IgnoreInput = !MainCPU.GetBUS().JOY_IO.Controller1.IgnoreInput;
                    if (MainCPU.GetBUS().JOY_IO.Controller1.IgnoreInput) {
                        Console.WriteLine("Controller inputs ignored");
                    } else {
                        Console.WriteLine("Controller inputs not ignored");
                    }
                    break;

                case Keys.F2:
                    Console.WriteLine("Resetting...");
                    MainCPU.Reset();
                    break;

                case Keys.K:
                  /* Does not work on this thread. TODO: Move to main thread with the UI
                 //We borrow some functionality from Windows Forms
                    System.Windows.Forms.FolderBrowserDialog folderBrowserDialog = new System.Windows.Forms.FolderBrowserDialog();
                    folderBrowserDialog.Description = "Please Select a Game Folder to Swap";
                    folderBrowserDialog.UseDescriptionForTitle = true;
                    if (folderBrowserDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) {
                        Console.WriteLine("Swapping with: " + Path.GetFileName(folderBrowserDialog.SelectedPath));
                        CPU.BUS.CDROM.SwapDisk(folderBrowserDialog.SelectedPath);
                    }
                 */
                   break;
            }

            Thread.Sleep(100);       
            Console.ForegroundColor = previousColor;
        }

        protected override void OnUnload() {
            CPUWrapper.DisposeCPU();
            GLRenderBackend.Destroy();
            base.OnUnload();
            Instance = null;
        }
    }
}
