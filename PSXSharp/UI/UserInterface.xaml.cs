using PSXSharp.UI;
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using MessageBox = System.Windows.Forms.MessageBox;

namespace PSXSharp {
    /// <summary>
    /// Interaction logic for UserInterface.xaml
    /// </summary>
    public partial class UserInterface : System.Windows.Window {
        //PSX_OpenTK MainEmu;
       
        string[] GamesFolders;
        string BootPath;
        bool IsEXE;
        const string COPYRIGHT = "Sony Computer Entertainment Inc.";
        bool HasValidBios;  
        Settings UserSettings;
        Thread EmulatorThread;

        public UserInterface() {
            InitializeComponent();

            EmulatorThread = new Thread(() => StartEmulator());
            Console.ForegroundColor = ConsoleColor.White;

            this.Title = "PSXSharp";
            this.ResizeMode = ResizeMode.NoResize;

            LoadSettings();

            if (!UserSettings.HasBios) {
                loadBios();
            } 

            HasValidBios = IsValidBios(UserSettings.BIOSPath);  

            if (UserSettings.HasGames) {
                Console.WriteLine("Found game folder");
                listGames(UserSettings.GamesFolderPath);
            } else {
                Console.WriteLine("No game folder found, please import your game folder");
            }
        }

        private void LoadSettings() {
            try {
                byte[] target = File.ReadAllBytes("Settings.bin");
                UserSettings = JsonSerializer.Deserialize<Settings>(new ReadOnlySpan<byte>(target));
                Console.WriteLine("Found Settings");
            } catch (Exception ex) {
                switch (ex) {
                    case FileNotFoundException: //Not found
                    case JsonException:         //Invalid
                        Console.WriteLine("Creating new settings");
                        UserSettings = new Settings();
                        break;
                    default:
                        Console.WriteLine("Fatal: " + ex.ToString());
                        DisableAll();
                        return;
                }
            }
        }

        private void DisableAll() {
            GameList.IsHitTestVisible = false;
            PlayButton.IsHitTestVisible = false;   
            ImportButton.IsHitTestVisible = false;
        }

        private void loadBios() {
            using (OpenFileDialog openFileDialog = new OpenFileDialog()) {
                openFileDialog.Title = "Select a PSX BIOS file";
                if (openFileDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK && 
                    !string.IsNullOrWhiteSpace(openFileDialog.FileName)) {
                    UserSettings.BIOSPath = openFileDialog.FileName;
                }
            }
        }
        private bool IsValidBios(string BIOSpath) {
            //All valid BIOSes should, hopefully, contain "Sony Computer Entertainment Inc." copyright string
            ReadOnlySpan<byte> data;
            try {
                data = File.ReadAllBytes(BIOSpath);
                data = data.Slice(0x108, COPYRIGHT.Length);
            }
            catch (ArgumentOutOfRangeException ex) {
                return false;                   //Invalid file
            }
            catch (ArgumentNullException ex) {  //When Hit cancel on dialog 
                return false;
            }
            string biosString = Encoding.ASCII.GetString(data);
            if (biosString.Equals(COPYRIGHT)) {
                Console.WriteLine("Found a valid BIOS!");
                return true;
            } else {
                Console.WriteLine("Selected BIOS is invalid");
                return false;
            }
        }
  
        private void PlayButton_Click(object sender, RoutedEventArgs e) {

            if (!HasValidBios) {
                loadBios();
                HasValidBios = IsValidBios(UserSettings.BIOSPath);
                if (!HasValidBios) {
                    return; //Refuse to run if not valid
                }
            }

            if(GameList.SelectedIndex < 0 || GameList.SelectedItem.Equals("Games go here")) {
                BootPath = null;
            } else {
                BootPath = UserSettings.GamesFolderPath + @"\" + GameList.SelectedItem;
            }
            Boot();
        }
        private void ImportButton_Click(object sender, RoutedEventArgs e) {
            if (EmulatorThread.IsAlive) {
                MessageBox.Show(
                    "Please close the emulator before loading games.",
                    "Emulator is already running",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                    );
                return;
            }

            using (var fbd = new FolderBrowserDialog()) {
                DialogResult result = fbd.ShowDialog();
                if (result == System.Windows.Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(fbd.SelectedPath)) {
                    UserSettings.GamesFolderPath = fbd.SelectedPath;
                    Console.WriteLine("Reading games folder...");
                    listGames(UserSettings.GamesFolderPath);
                }
            }
        }
        private void listGames(string path) {
            GameList.Items.Clear();
            if (Directory.Exists(path)) {
                GamesFolders = Directory.GetDirectories(path);
                Console.WriteLine("Found " + GamesFolders.Length + " games");
                foreach (string gameFolder in GamesFolders) {
                    GameList.Items.Add(Path.GetFileName(gameFolder));
                }
            } else {
                GameList.Items.Add("Games go here");
            }
        }
        private void OnClose(object sender, EventArgs e) {
            SaveSettings();
        }
        
        private void Boot() {
            SaveSettings();     //Save before booting to prevent losing settings if the emulator crashed
            if (!EmulatorThread.IsAlive) {
                EmulatorThread = new Thread(() => StartEmulator());
                EmulatorThread.Start();
            } else {
                MessageBox.Show(
                    "Please close the emulator before booting again.",
                    "Emulator is already running",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                    );
            }
        }

        private void StartEmulator() {
            Console.WriteLine("Emulation Thread ID: {0}", Thread.CurrentThread.ManagedThreadId);
            Console.ForegroundColor = ConsoleColor.Green;
            PSX_OpenTK MainEmu = new PSX_OpenTK(UserSettings.BIOSPath, BootPath, IsEXE);        /* Emulation loop starts */
            ResetBootConfig();
            GC.Collect();
            //Console.Clear();
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("Emulation thread terminated.");
        }

        private void Window_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) {
            GameList.UnselectAll();
        }
        private void ResetBootConfig() {
            BootPath = null;
            IsEXE = false;
        }
        private void OnDrop(object sender, System.Windows.DragEventArgs e) {   
            if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) {
                string[] files = (string[])e.Data.GetData(System.Windows.DataFormats.FileDrop);
                string extension = Path.GetExtension(files[0]).ToLower();
                if (extension.Equals(".exe") || extension.Equals(".ps-exe")) {
                    Console.WriteLine("Booting Executable: " + files[0]);
                    IsEXE = true;
                    BootPath = files[0];
                    Boot();
                }else if (extension.Equals(".bin") || extension.Equals(".iso")) {
                    Console.WriteLine("Booting Direct Binary: " + files[0]);
                    //Boot();
                }
            }
        }
        
        private void SaveSettings() {
            byte[] serialized = JsonSerializer.SerializeToUtf8Bytes(UserSettings,
                 new JsonSerializerOptions { WriteIndented = false, IgnoreNullValues = false });
            File.WriteAllBytes("Settings.bin", serialized);
        }
    }
}
