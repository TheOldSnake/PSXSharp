using PSXSharp.Core.Interpreter;
using PSXSharp.Core.x64_Recompiler;
using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PSXSharp.Core {
    public static class CPUWrapper {
        private static CPU? CPU;
        private static string? CPUType;
        public static BUS BUS => CPU.GetBUS();
        public static CPU CreateInstance(bool isRecompiler, bool isX64, bool isBootingEXE, string bootPath, BUS bus) {
            if (CPU != null) {
                throw new Exception("Cannot create more than one CPU");
            }

            if (isRecompiler) {
                if (isX64) {
                    if (RuntimeInformation.ProcessArchitecture != Architecture.X64 || !RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
                        MessageBox.Show("Unsupported OS/Architecture.\nProgram will exit.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        throw new NotSupportedException();
                    }

                    CPU = CPU_x64_Recompiler.GetOrCreateCPU(isBootingEXE, bootPath, bus);
                    CPUType = "x64 JIT";

                } else {
                    CPU = new CPU_MSIL_Recompiler(isBootingEXE, bootPath, bus);
                    CPUType = "MSIL JIT";
                }

            } else {
                CPU = new CPU_Interpreter(isBootingEXE, bootPath, bus);
                CPUType = "Interpreter";
            }

            return CPU;
        }

        public static CPU GetCPUInstance() {
            if (CPU == null) {
                throw new Exception("CPU was not created");
            }
            return CPU;
        }

        public static string GetCPUType() { 
            return CPUType;
        }

        public static void DisposeCPU() {
            if (CPU != null && CPU.GetType() == typeof(CPU_x64_Recompiler)) {
                CPU_x64_Recompiler cpu = (CPU_x64_Recompiler)CPU;
                cpu.Dispose();
            }

            CPU = null;
        }
    }
}
