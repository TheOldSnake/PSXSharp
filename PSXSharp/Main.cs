using System;
using System.Runtime.InteropServices;
using System.Windows;

namespace PSXSharp {
    internal class MainProgram {
        const int CONSOLE_WIDTH = 80;
        const int CONSOLE_HEIGHT = 20;

        [STAThread]
        static void Main(string[] args) {
            Console.SetWindowSize(CONSOLE_WIDTH, CONSOLE_HEIGHT);
            Console.Title = "TTY Console";
            Console.WriteLine($".NET Version: {Environment.Version}");
            Console.WriteLine($"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
            Application app = new Application();
            app.Run(new UserInterface());    //Launch UI
            Environment.Exit(0);
        }
    }
}


