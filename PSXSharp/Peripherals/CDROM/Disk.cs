using System.IO;
using System.Text;
using System;

namespace PSXSharp.Peripherals.CDROM {
    public class Disk {
        public Track[] Tracks;
        public bool HasCue;
        public bool HasDataTracks;
        public bool HasAudioTracks;
        public bool IsAudioDisk => HasAudioTracks && !HasDataTracks;
        public bool IsValid => Tracks != null;
        public bool IsPresent = false;
        public Disk(string? folderPath) {
            ConsoleColor previousColor = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.DarkYellow;

            if (!string.IsNullOrEmpty(folderPath)) {
                IsPresent = true;
                Tracks = BuildTracks(folderPath);
            } else {
                Console.WriteLine("[CDROM] No game path provided");
            }

            Console.ForegroundColor = previousColor;
        }

        private Track[] BuildTracks(string path) {
            string[] rawFiles = Directory.GetFiles(path);
            string cuePath = "";
            Track[] tracks;            
            for (int i = 0; i < rawFiles.Length; i++) {
                if (Path.GetExtension(rawFiles[i]).ToLower().Equals(".cue")) { //Find Cue sheet
                    cuePath = rawFiles[i];
                    Console.WriteLine("[TrackBuilder] Found Cue sheet!");
                    HasCue = true;
                    break;
                }
            }
            if (HasCue) {
                string cueSheet = File.ReadAllText(cuePath);
                string[] filesInCue = cueSheet.Split("FILE");
                tracks = new Track[filesInCue.Length - 1];
                ReadOnlySpan<string> spanOfCueFiles = new ReadOnlySpan<string>(filesInCue).Slice(1);   //Skip 0 as it is nothing
                ParseCue(spanOfCueFiles, path, ref tracks);
                return tracks;
            } else {
                //Find first valid bin
                Console.WriteLine("[TrackBuilder] Could not find a Cue sheet, CD-DA audio will not be played");
                int indexOfDataTrack = FindFirstValidBinary(rawFiles);
                if (indexOfDataTrack < 0) {
                    HasDataTracks = false;
                    HasAudioTracks = false;
                    Console.WriteLine("[TrackBuilder] Couldn't find a valid binary");
                    return null;    
                } else {
                    HasDataTracks = true;
                    Track dataTrack = new Track(rawFiles[indexOfDataTrack], false, 01, "00:00:00"); //I'm really assuming stuff 
                    dataTrack.Length = (int)new FileInfo(dataTrack.FilePath).Length;
                    return new Track[] { dataTrack };
                }
            } 
        }
        private int FindFirstValidBinary(string[] rawFiles) {
            for (int i = 0; i < rawFiles.Length; i++) {
                string extension = Path.GetExtension(rawFiles[i]).ToLower();
                if (extension.Equals(".bin") || extension.Equals(".iso")) {     //Check PLAYSTAION String for a valid CD-XA track
                    if (IsValidBin(rawFiles[i])) {
                        return i;
                    }
                }
            }
            return -1;
        }
        private bool IsValidBin(string path) {
            ReadOnlySpan<byte> data = File.ReadAllBytes(path);
            try {
                data = data.Slice((16 * 0x930) + 0x20, 11);
                string ID = Encoding.ASCII.GetString(data);
                if (ID.Equals("PLAYSTATION")) {
                    return true;
                }
            } catch (ArgumentOutOfRangeException ex) {
                return false;
            }
            return false;
        }
        private void ParseCue(ReadOnlySpan<string> filesInCue, string gameFolder, ref Track[] tracks) {
            int offset = 0;

            for (int i = 0; i < filesInCue.Length; i++) {
                string fileName = GetFileName(filesInCue[i]);
                string filePath = gameFolder + @"\" + fileName;
                if (!File.Exists(filePath)) {
                    Console.WriteLine("[TrackBuilder] Warning, could not find the file: " + filePath);
                    continue;
                }
                string[] indexes = filesInCue[i].Split("INDEX");
                string index1MSF = "";
                bool hasPregap = false;
                for (int j = 1; j < indexes.Length; j++) {
                    string[] details = indexes[j].Split(" ");
                    if (details[1].Equals("00")) {
                        hasPregap = true;
                    }
                    if (details[1].Equals("01")) {
                        index1MSF = details[2];
                    }
                }
                if (indexes.Length > 3) {
                    Console.WriteLine("[TrackBuilder] Found file with multiple indexes!");
                }
                if (filesInCue[i].Contains("2352")) {
                    HasDataTracks = true;
                }
                if (filesInCue[i].Contains("AUDIO")) {  //Could be audio disk but could also be game disk with audio tracks
                    HasAudioTracks = true;
                }
                tracks[i] = new Track(filePath, filesInCue[i].Contains("AUDIO"), i + 1, index1MSF);
                string[] initialMSF = index1MSF.Split(":");

                int M; int S; int F;
                (M, S, F) = BytesToMSF(offset);

                int cueM = 0;
                int cueS = 0;
                int cueF = 0;
                bool valid = (int.TryParse(initialMSF[0], out cueM) && int.TryParse(initialMSF[1], out cueS) && int.TryParse(initialMSF[2], out cueF));
                if (!valid) {
                    Console.WriteLine("[TrackBuilder] Cue Parse error, aborting..");
                    HasCue = false;
                    return;
                }

                M += hasPregap ? cueM : 0;
                S += hasPregap ? cueS : 0;
                F += hasPregap ? cueF : 0;

                if (F >= 75) { S += (F / 75); F = F % 75; }
                if (S >= 60) { M += (S / 60); S = S % 60; }
                tracks[i].M = M;
                tracks[i].S = S;
                tracks[i].F = F;

                int length = ((int)new FileInfo(filePath).Length) - (hasPregap ? 0 : ((cueM * 60 * 75) + (cueS * 75) + cueF) * 0x930);
                tracks[i].Length = length;

                Console.WriteLine("------------------------------------------------------------");
                Console.WriteLine("[TrackBuilder] Added new track: ");
                Console.WriteLine("[TrackBuilder] Path: " + filePath);
                Console.WriteLine("[TrackBuilder] CUE Index 01: " + index1MSF.Replace("\n", ""));
                Console.WriteLine("[TrackBuilder] isAudio: " + filesInCue[i].Contains("AUDIO"));
                Console.WriteLine("[TrackBuilder] Number: " + (i + 1).ToString().PadLeft(2, '0'));
                Console.WriteLine("[TrackBuilder] Start: " + M.ToString().PadLeft(2, '0') + ":" + S.ToString().PadLeft(2, '0') + ":" + F.ToString().PadLeft(2, '0'));
                Console.WriteLine("[TrackBuilder] Length: " + BytesToMSF(length));
                Console.WriteLine("------------------------------------------------------------");

                offset += length;
            }
        }
        private string GetFileName(string firstLine) {
            string[] splitted = firstLine.Split('"');
            return splitted[1];
        }
        private static (int, int, int) BytesToMSF(int totalSize) {
            int totalFrames = totalSize / 2352;
            int M = totalFrames / (60 * 75);
            int S = (totalFrames % (60 * 75)) / 75;
            int F = (totalFrames % (60 * 75)) % 75;
            return (M, S, F);
        }
    }
}
