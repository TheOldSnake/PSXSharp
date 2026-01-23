using PSXSharp.Core.Interpreter;
using PSXSharp.Core.x64_Recompiler;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using static PSXSharp.PSX_OpenTK;

namespace PSXSharp.Core {
    public static class CPUWrapper {
        private static CPU? CPU;
        private static CPUType CpuType;
        public static string? CPUTypeName { get; private set; }

        public static BUS BUS => CPU.GetBUS();
        public static bool IsCompatibleWithX64JIT => RuntimeInformation.ProcessArchitecture == Architecture.X64 && RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        public static CPU CreateInstance(CPUType cpuType, bool isBootingEXE, string bootPath, BUS bus) {
            if (CPU != null) {
                throw new Exception("Cannot create more than one CPU");
            }

            switch (cpuType) {
                case CPUType.Interpreter:
                    CPU = new CPU_Interpreter(isBootingEXE, bootPath, bus);
                    CPUTypeName = "Interpreter";
                    break;          
                    
                case CPUType.MSILRecompiler:
                    CPU = new CPU_Interpreter(isBootingEXE, bootPath, bus);
                    CPUTypeName = "MSIL JIT";
                    break;            

                case CPUType.x64Recompiler:
                    if (!IsCompatibleWithX64JIT) {
                        MessageBox.Show("Unsupported OS/Architecture for x64 JIT.\nEmulator will exit.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        Environment.Exit(0);
                    }

                    CPU = CPU_x64_Recompiler.GetCPU(isBootingEXE, bootPath, bus);
                    CPUTypeName = "x64 JIT";
                    break;            

                default:
                    throw new UnreachableException($"Unreachable case");
            }

            return CPU;
        }

        public static CPU GetCPUInstance() {
            if (CPU == null) {
                throw new Exception("CPU was not created");
            }
            return CPU;
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
