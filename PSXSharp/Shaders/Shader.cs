using OpenTK.Compute.OpenCL;
using OpenTK.Graphics.OpenGL4;
using System;

namespace PSXSharp.Shaders {
    public partial class Shader {  
        public int Program;
        public Shader(string vert, string frag) {
            int vertexShader = GL.CreateShader(ShaderType.VertexShader);    //Create a vertex shader and get a pointer
            GL.ShaderSource(vertexShader, vert);                            //Bind the source code string
            CompileShader(vertexShader);                                    //Compile and check for errors

            int fragmentShader = GL.CreateShader(ShaderType.FragmentShader);    //Same thing for fragment shader
            GL.ShaderSource(fragmentShader, frag);
            CompileShader(fragmentShader);

            //Create a program, store the pointer to it, and attach to it both shaders
            Program = GL.CreateProgram();
            GL.AttachShader(Program, vertexShader);
            GL.AttachShader(Program, fragmentShader);

            //Link the program
            GL.LinkProgram(Program);
            GL.GetProgram(Program, GetProgramParameterName.LinkStatus, out var code);    // Check for linking errors
            if (code != 1) {
                throw new Exception($"Error occurred whilst linking Program({Program})");
            }

            //After linking them the indivisual shaders are not needed, they have been copied to the program
            //Clean up
            GL.DetachShader(Program, vertexShader);
            GL.DetachShader(Program, fragmentShader);
            GL.DeleteShader(fragmentShader);
            GL.DeleteShader(vertexShader);
        }
        private void CompileShader(int shader) {
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
        public void Use() {
            GL.UseProgram(Program);
        }
    }
}
