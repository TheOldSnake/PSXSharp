using OpenTK.Compute.OpenCL;
using OpenTK.Graphics.OpenGL4;
using System;

namespace PSXSharp.Shaders {
    public class Shader {  
        public readonly int MainProgram;
        public readonly int ComputeProgram;

        public Shader(string vert, string frag, string vramTransfer) {
            int vertexShader = GL.CreateShader(ShaderType.VertexShader);        //Create a vertex shader and get a pointer
            GL.ShaderSource(vertexShader, vert);                                //Bind the source code string
            CompileShader(vertexShader);                                        //Compile and check for errors

            int fragmentShader = GL.CreateShader(ShaderType.FragmentShader);    //Same thing for fragment shader
            GL.ShaderSource(fragmentShader, frag);
            CompileShader(fragmentShader);

            int vramTransferShader = GL.CreateShader(ShaderType.ComputeShader);     //Create a compute shader for VRAM Copy commands
            GL.ShaderSource(vramTransferShader, vramTransfer);
            CompileShader(vramTransferShader);

            //Create the programs and attach the shaders
            MainProgram = GL.CreateProgram();
            ComputeProgram = GL.CreateProgram();

            GL.AttachShader(MainProgram, vertexShader);
            GL.AttachShader(MainProgram, fragmentShader);
            GL.AttachShader(ComputeProgram, vramTransferShader);

            //Link both programs
            Link(MainProgram);
            Link(ComputeProgram);

            //After linking them the indivisual shaders are not needed, they have been copied to the program
            //Clean up
            GL.DetachShader(MainProgram, vertexShader);
            GL.DetachShader(MainProgram, fragmentShader);
            GL.DetachShader(ComputeProgram, vramTransferShader);
            GL.DeleteShader(fragmentShader);
            GL.DeleteShader(vertexShader);
            GL.DeleteShader(vramTransferShader);
        }

        private static void CompileShader(int shader) {
            GL.CompileShader(shader);
            GL.GetShader(shader, ShaderParameter.CompileStatus, out int code);  //Check for compilation errors
            if (code != (int)All.True) {
                string infoLog = GL.GetShaderInfoLog(shader);
                throw new Exception($"Error occurred whilst compiling Shader({shader}).\n\n{infoLog}");
            } else {
                ConsoleColor previousColor = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("[OpenGL] Shader compiled!");
                Console.ForegroundColor = previousColor;
            }
        }

        private static void Link(int program) {
            //Link the ComputeProgram
            GL.LinkProgram(program);
            GL.GetProgram(program, GetProgramParameterName.LinkStatus, out var code);    // Check for linking errors
            if (code != (int)All.True) {
                throw new Exception($"Error occurred whilst linking Program({program})");
            }
        }
    }
}
