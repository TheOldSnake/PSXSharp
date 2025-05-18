using PSXSharp.Core;
using PSXSharp.Core.x64_Recompiler;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PSXSharp {
    public unsafe class CD_ROM {
        public Range range = new Range(0x1F801800, 4);
        const int OneSecond = 33868800;
        Action CDROMCallback;

        public enum Flags {
            INT1 = 1, //INT1 Received SECOND (or further) response to ReadS/ReadN (and Play+Report)
            INT2 = 2, //INT2 Received SECOND response (complete/done)
            INT3 = 3, //INT3 Received FIRST response (ack)
            INT4 = 4, //INT4 DataEnd(when Play/Forward reaches end of disk)
            INT5 = 5, //INT5 So many things, but mainly for errors
            INT6 = 6, //NA
            INT7 = 7, //NA
            Dummy = 8,
        }
        public enum Delays { /* Magic Numbers */
            Zero = 0,
            INT1_SingleSpeed = 0x006e1cd,
            INT1_DoubleSpeed = (int)(OneSecond * 0.65666),        //650ms for the speed up and seek, + 6.66ms to read
            INT2_GetID = 0x0004a00,
            INT2_Pause_SingleSpeed = (int)(OneSecond * 0.070),    //70ms
            INT2_Pause_DoubleSpeed = (int)(OneSecond * 0.035),    //35ms
            INT2_PauseWhenPaused = 0x0001df2,
            INT2_Stop_SingleSpeed = 0x0d38aca,
            INT2_Stop_DoubleSpeed = 0x18a6076,
            INT2_StopWhenStopped = 0x0001d7b,
            INT2LongSeek = OneSecond * 2,                       //At least 1s
            INT2_Init = (int)(OneSecond * 0.120),               //120ms? 
            INT3_Init = (int)(OneSecond * 0.002),               //2ms
            INT3_General = 0x000c4e1,
            INT3_GetStatWhenStopped = 0x0005cf4,
            //INT4 = Unknown ? 
            INT5 = 0x0004a00    //Same as GetId, seems to work
        }

        public enum Errors {
            SeekError = 0x04,
            DriveDoorOpen = 0x08,
            InvalidParameter = 0x10,
            InvalidNumberOfParameters = 0x20,
            InvalidCommand = 0x40,
            CannotRespondYet = 0x80
        }

        public enum CDROMState {
            Idle,
            Seeking,
            ReadingData,
            PlayingCDDA,
            SwappingDisk
        }

        //Status Register
        int Index;                 //0-1
        int ADPBUSY = 0;           //2
        int PRMEMPT = 1;           //3
        int PRMWRDY = 1;           //4
        int RSLRRDY = 0;           //5
        int DRQSTS = 0;            //6
        int BUSYSTS = 0;           //7

        CircularBuffer ResponseBuffer = new CircularBuffer(16);
        Queue<Byte> ParameterBuffer = new Queue<Byte>();
        Queue<Response> Responses = new Queue<Response>();
        
        byte[] SeekParameters = new byte[3];

        byte IRQ_Enable; //0-7
        byte IRQ_Flag;  //0-7

        byte stat = 0x2;  //Initially motor is on

        //Mode
        byte Mode;
        uint LastSize;
        bool AutoPause;  //TODO
        bool CDDAReport;
        bool ReadCDDASectors;    //Allow to Read (not play) CD-DA Sectors  

        int M, S, F;    
        int CurrentIndex; 
        int SkipRate = 0;
        bool SetLoc;
        bool IsReportableSector => (F % 10) == 0;   //10,20,30..etc

        /* CD Audio Volume */
        byte LeftCD_toLeft_SPU_Volume;
        byte LeftCD_toRight_SPU_Volume;
        byte RightCD_toRight_SPU_Volume;
        byte RightCD_toLeft_SPU_Volume;

        bool DoubleSpeed;

        CDROMState State;

        public byte CurrentCommand;
       // int PendingCommand = -1;
        int TransmissionDelay = 0;
 
        public bool LidOpen = false;

        private delegate*<CD_ROM, void>[] LookUpTable = new delegate*<CD_ROM, void>[0xFF + 1];
        public CDROMDataController DataController;
        long ReadRate = 0;
        uint SectorOffset = 0;  //Skip headers, etc

        bool SeekedP;
        bool SeekedL;
        int SwappingDelay = -1;
        public CD_ROM(string path, bool isDirectFile) {
            LoadLUT();
            DataController = new CDROMDataController(path);
            CDROMCallback = ResponseHandler;
        }

        public CD_ROM() {   //Overload for when booting EXEs
            //Stub for the CDROM Tests
            LoadLUT();
            DataController = new CDROMDataController(@"C:\Users\Old Snake\Desktop\PS1\ROMS\Archive");
            CDROMCallback = ResponseHandler;
        }

        private void LoadLUT() {
            //Fill the functions lookUpTable with illegal first, to be safe
            for (int i = 0; i < LookUpTable.Length; i++) {
                LookUpTable[i] = &Illegal;
            }
            //Add whatever I implemented manually
            LookUpTable[0x00] = &Sync;
            LookUpTable[0x01] = &GetStat;
            LookUpTable[0x02] = &Setloc;
            LookUpTable[0x03] = &Play;
            LookUpTable[0x04] = &Forward;
            LookUpTable[0x05] = &Backward;
            LookUpTable[0x06] = &ReadNS;
            LookUpTable[0x07] = &MotorOn;
            LookUpTable[0x08] = &Stop;
            LookUpTable[0x09] = &Pause;
            LookUpTable[0x0A] = &Init;
            LookUpTable[0x0B] = &Mute;
            LookUpTable[0x0C] = &Demute;
            LookUpTable[0x0D] = &SetFilter;
            LookUpTable[0x0E] = &SetMode;
            LookUpTable[0x10] = &GetLocL;
            LookUpTable[0x11] = &GetLocP;
            LookUpTable[0x13] = &GetTN;
            LookUpTable[0x14] = &GetTD;
            LookUpTable[0x15] = &SeekL;
            LookUpTable[0x16] = &SeekP;
            LookUpTable[0x19] = &Test;
            LookUpTable[0x1A] = &GetID;
            LookUpTable[0x1B] = &ReadNS;
            LookUpTable[0x1E] = &ReadTOC;
        }

        private static void Illegal(CD_ROM cdrom) {
            throw new Exception("Unknown CDROM command: " + cdrom.CurrentCommand.ToString("x"));
        }

        bool IsError;
        public void Controller(byte command) {
            //PendingCommand = command;
            /*TransmissionDelay = 1000;
            IsError = CurrentCommand == 0x16 && command == 0x1 && Responses.Count > 0; //It's not that simple...
            if (IsError) {
                for (int i = 0; i < Responses.Count; i++) {
                    if (!Responses.ElementAt(i).FinishedProcessing && Responses.ElementAt(i).interrupt == (int)Flags.INT3) {
                        Responses.ElementAt(i).NextState = CDROMState.Idle;
                    }
                }
            }*/

            Execute(command);
            ScheduleCDResponse();
        }

        public void Execute(int command) {
            CurrentCommand = (byte)command;
            /*if (IsError) {
                Responses = new Queue<Response>(Responses.Where(x => x.interrupt == (int)Flags.INT3));    //Keep only INT3
                LookUpTable[command](this);                                                             //Execute then error
                stat = 0x6;
                Error(this, Errors.InvalidParameter, OneSecond * 5); //5s
                Console.WriteLine("[CDROM] Error at command: 0x" + command.ToString("x"));
                return;
            }*/
            LookUpTable[command](this);
            //Console.WriteLine("[CDROM] Command: 0x" + command.ToString("x").ToUpper() + " - State: " + State);
        }

        public void SwapDisk(string path) {
            SwappingDelay = OneSecond;       //Delay for disc lid open
            DataController.LoadDisk(path);
            State = CDROMState.SwappingDisk;
            LidOpen = true;
        }

        private byte CDROM_Status() {
            PRMEMPT = ParameterBuffer.Count == 0 ? 1 : 0;
            PRMWRDY = ParameterBuffer.Count < 16 ? 1 : 0;
            RSLRRDY = ResponseBuffer.HasUnreadData ? 1 : 0;
            DRQSTS = (DataController.DataFifo.Count > 0 && DataController.BFRD == 1) ? 1 : 0;
            BUSYSTS = TransmissionDelay > 0 ? 1 : 0;
            byte status = (byte)((BUSYSTS << 7) | (DRQSTS << 6)  | (RSLRRDY << 5) | (PRMWRDY << 4) | (PRMEMPT << 3) | (ADPBUSY << 2) | Index);
            return status;
        }

        public void StoreByte(uint address, byte value) {
            uint offset = address - range.start;
            switch (offset) {
                case 0: Index = value & 0x3; break; //Status register, all mirrors
                case 1:
                    switch (Index) {
                        case 0: Controller(value); break;
                        case 3: RightCD_toRight_SPU_Volume = value; break;
                        default: throw new Exception("Unknown Index (" + Index + ")" + " at CRROM command register");
                    }
                    break;

                case 2:
                    switch (Index) {
                        case 0: ParameterBuffer.Enqueue(value); break;
                        case 1: IRQ_Enable = value; break;
                        case 2: LeftCD_toLeft_SPU_Volume = value; break;
                        case 3: RightCD_toLeft_SPU_Volume = value; break;
                        default: throw new Exception("Unknown Index (" + Index + ")" + " at CRROM IRQ enable register");
                    }
                    break;

                case 3:
                    switch (Index) {
                        case 0: RequestRegister(value); break;
                        case 1: InterruptFlagRegister(value); break;
                        case 2: LeftCD_toRight_SPU_Volume = value; break;
                        case 3: ApplyVolume(value); break;
                        default: throw new Exception("Unknown Index (" + Index + ")" + " at CRROM IRQ flag register");
                    }
                    break;

                default: throw new Exception("Unhandled store at CRROM offset: " + offset + " index: " + Index);
            }
        }

        public byte LoadByte(uint address) {
            uint offset = address - range.start;
            switch (offset) {
                case 0: return CDROM_Status();                          //Status register, all indexes are mirrors
                case 1: return ResponseBuffer.ReadNext();                //Response fifo, all indexes are mirrors
                case 2: return DataController.ReadByte();               //Data fifo, all indexes are mirrors
                case 3:
                    switch (Index) {
                        case 0:
                        case 2: return (byte)(IRQ_Enable | 0xe0);   //0-4 > INT enable , the rest are 1s
                        case 1:
                        case 3: return (byte)(IRQ_Flag | 0xe0);    //0-4 > INT flag , the rest are 1s
                        default: throw new Exception("Unknown Index (" + Index + ")" + " at CRROM IRQ flag register");
                    }
                default: throw new Exception("Unhandled read at CRROM register: " + offset + " index: " + Index);
            }
        }

        private void RequestRegister(byte value) {
            if (((value >> 7) & 1) == 1) {  //Request data
                //Console.WriteLine("DATA REQUESTED");
                DataController.BFRD = 1;
                if (DataController.DataFifo.Count > 0) { return; }
                DataController.MoveSectorToDataFifo();
            } else {
                //Console.WriteLine("FIFO CLEARED");
                DataController.BFRD = 0;
                DataController.DataFifo.Clear();
            }
        }
        private void InterruptFlagRegister(byte value) {
            IRQ_Flag &= (byte)~(value & 0x1F);
            if (((value >> 6) & 1) == 1) {
                ParameterBuffer.Clear();
            }

            //When ack comes very late (that the next response is already done) we should still add ~350us delay for CPU/CDROM communication
            if ((IRQ_Flag & 0x7) == 0 && Responses.Count > 0) {
                if (Responses.Peek().FinishedProcessing) {
                    Responses.Peek().delay = (int)Math.Ceiling(OneSecond * 0.00035);    
                }
            }
        }

        private void ApplyVolume(byte value) {
            bool isMute = (value & 1) != 0;
            bool applyVolume = ((value >> 5) & 1) != 0;
            if (isMute) {
                DataController.CurrentVolume.LtoL = 0;
                DataController.CurrentVolume.LtoR = 0;
                DataController.CurrentVolume.RtoL = 0;
                DataController.CurrentVolume.RtoR = 0;
            } else if (applyVolume) {
                DataController.CurrentVolume.LtoL = LeftCD_toLeft_SPU_Volume;
                DataController.CurrentVolume.LtoR = LeftCD_toRight_SPU_Volume;
                DataController.CurrentVolume.RtoL = RightCD_toLeft_SPU_Volume;
                DataController.CurrentVolume.RtoR = RightCD_toRight_SPU_Volume;
            }
        }

        private static void Sync(CD_ROM cdrom) {
            Error(cdrom, Errors.InvalidCommand);
        }

        private static void Test(CD_ROM cdrom) {
            if (cdrom.ParameterBuffer.Count == 0) {
                Error(cdrom, Errors.InvalidNumberOfParameters);
                return;
            }
            byte parameter = cdrom.ParameterBuffer.Dequeue();
            switch (parameter) {
                case 0x04: ReadSCEx(cdrom); break;
                case 0x05: GetSCExCounters(cdrom); break;
                case 0x20: GetDateAndVersion(cdrom); break;
                case 0xFF: Error(cdrom, Errors.InvalidParameter); break;
                default: throw new Exception("[CDROM] Test command: unknown parameter: " + parameter.ToString("x"));
            }
        }

        private static void GetSCExCounters(CD_ROM cdrom) {
            //Typically, the values are "01h,01h" for Licensed PSX Data CDs, or "00h,00h" for disk missing, unlicensed data CDs, Audio CDs.
            Response ack = new Response(new byte[] { 0x1,0x1 }, Delays.INT3_General, Flags.INT3, cdrom.State);
            cdrom.Responses.Enqueue(ack);

            //Seems like Spyro Year of The Dragon uses this command as one of its (many) anti piracy tricks:
            //It tries to read SCEx string, but since the head is not in the lead-in area (where the strings are stored)
            //The command should fail and return 00h, 00h, if it doesn't, it detects that a modchip is simulating SCEx strings and decides to lock
        }

        private static void ReadSCEx(CD_ROM cdrom) {
            //19h,04h --> INT3(stat) ;Read SCEx string (and force motor on)
            Response ack = new Response(new byte[] { cdrom.stat }, Delays.INT3_General, Flags.INT3, cdrom.State);
            cdrom.Responses.Enqueue(ack);
            cdrom.stat |= 0x2; //Motor On
        }

        private static void MotorOn(CD_ROM cdrom) {
            //Activates the drive motor, works ONLY if the motor was off (otherwise fails with INT5(stat,20h);
            //that error code would normally indicate "wrong number of parameters", but means "motor already on" in this case). 
            if ((cdrom.stat & 1) != 0) {    //Check if Motor is already on
                Console.WriteLine("[CDROM] MotorOn error!");
                Error(cdrom,Errors.InvalidNumberOfParameters);
            } else {
                Console.WriteLine("[CDROM] MotorOn!");
                Response ack = new Response(new byte[] { cdrom.stat }, Delays.INT3_General, Flags.INT3, cdrom.State);
                cdrom.stat |= 0x2;  //Motor On
                Response done = new Response(new byte[] { cdrom.stat }, Delays.INT2_GetID, Flags.INT2, cdrom.State);    //Same dealy as GetID, maybe?
                cdrom.Responses.Enqueue(ack);
                cdrom.Responses.Enqueue(done);
            }
        }
        private static void ReadTOC(CD_ROM cdrom) {
            //Caution: Supported only in BIOS version vC1 and up. Not supported in vC0.
            Response ack = new Response(new byte[] { cdrom.stat }, Delays.INT3_General, Flags.INT3, cdrom.State);
            Response done = new Response(new byte[] { cdrom.stat }, Delays.INT2_GetID, Flags.INT2, cdrom.State);
            cdrom.Responses.Enqueue(ack);
            cdrom.Responses.Enqueue(done);
        }

        private static void Forward(CD_ROM cdrom) {
            if (cdrom.State != CDROMState.PlayingCDDA) {
                Error(cdrom, Errors.CannotRespondYet);
                return;
            }
            cdrom.SkipRate += 15;
            Response ack = new Response(new byte[] { cdrom.stat }, Delays.INT3_General, Flags.INT3, cdrom.State);
            cdrom.Responses.Enqueue(ack);
        }

        private static void Backward(CD_ROM cdrom) {
            if (cdrom.State != CDROMState.PlayingCDDA) {
                Error(cdrom, Errors.CannotRespondYet);
                return;
            }
            cdrom.SkipRate -= 15;
            Response ack = new Response(new byte[] { cdrom.stat }, Delays.INT3_General, Flags.INT3, cdrom.State);
            cdrom.Responses.Enqueue(ack);
        }

        private static void GetLocL(CD_ROM cdrom) {       
            if (cdrom.SeekedL || cdrom.SeekedP || cdrom.LidOpen) {   //Error if a seek has been done but no read, also if lid is opened
                Error(cdrom, Errors.CannotRespondYet);
                return;
            }

            //INT3(amm,ass,asect,mode,file,channel,sm,ci)
            byte[] header = cdrom.DataController.LastSectorHeader;      //Are MSF already in BCD?
            byte[] subHeader = cdrom.DataController.LastSectorSubHeader;

            Response ack = new Response(new byte[] { header[0], header[1], header[2], header[3], 
                subHeader[0], subHeader[1], subHeader[2], subHeader[3]}, Delays.INT3_General, Flags.INT3, cdrom.State);
            cdrom.Responses.Enqueue(ack);
        }

        private static void GetLocP(CD_ROM cdrom) {
            if (cdrom.LidOpen) {                        //Error if if lid is opened
                Error(cdrom, Errors.DriveDoorOpen);
                return;
            }

            //GetlocP - Command 11h - INT3(track,index,mm,ss,sect,amm,ass,asect) all BCD
            byte currentTrack = (byte)cdrom.DataController.SelectedTrackNumber;

            //Index is 0 if in pregap, otherwise I should actually get the index number instead of hardcoding 1
            //...But most games are having only one index (01) other than the pregap (00)
            bool pregap = cdrom.CurrentIndex < cdrom.DataController.Disk.Tracks[currentTrack - 1].Start;

            byte index = DecToBcd((byte)(pregap ? 0x00 : 0x01));
            byte mm;
            byte ss;
            byte ff;
            byte amm;
            byte ass;
            byte aff;

            int cdm, cds, cdf;

            if (cdrom.SeekedP) {   //Distance from the seeked position will be up to 50 frames around this location.
                (cdm , cds, cdf) = BytesToMSF(cdrom.CurrentIndex - (25 * 0x930) + (150 * 0x930)); 

            }else if (cdrom.SeekedL) { //Distance from the seeked position will be up to 10 frames around this location
                (cdm, cds, cdf) = BytesToMSF(cdrom.CurrentIndex - (10 * 0x930) + (150 * 0x930));

            }else {
                (cdm, cds, cdf) = (cdrom.M, cdrom.S, cdrom.F);
            }
            int absoluteFrame = (cdm * 60 * 75) + (cds * 75) + cdf - 150;  //In frames
            int trackFrame = 
                (cdrom.DataController.Disk.Tracks[cdrom.DataController.SelectedTrackNumber - 1].M * 60 * 75) +
                (cdrom.DataController.Disk.Tracks[cdrom.DataController.SelectedTrackNumber - 1].S * 75) + 
                (cdrom.DataController.Disk.Tracks[cdrom.DataController.SelectedTrackNumber - 1].F);     //In frames

            int relativeM;
            int relativeS;
            int relativeF;
            
            //taking the difference after converting everything to frames, I don't like having negatives in M/S/F
            (relativeM, relativeS, relativeF) = BytesToMSF((pregap? trackFrame - absoluteFrame : absoluteFrame - trackFrame) * 0x930);

            currentTrack = DecToBcd(currentTrack);
            mm = DecToBcd((byte)relativeM);
            ss = DecToBcd((byte)relativeS);
            ff = DecToBcd((byte)relativeF);
            amm = DecToBcd((byte)cdm);
            ass = DecToBcd((byte)cds);
            aff = DecToBcd((byte)cdf);
            //Console.WriteLine("GetLocP -> ALBA:" + amm.ToString("x") + ":" + ass.ToString("x") + ":" + aff.ToString("x"));
            //Console.WriteLine("GetLocP -> LBA:" + mm.ToString("x") + ":" + ss.ToString("x") + ":" + ff.ToString("x"));

            Response ack = new Response(new byte[] { currentTrack, index, mm, ss, ff, amm, ass, aff }, Delays.INT3_General, Flags.INT3, cdrom.State);
            cdrom.Responses.Enqueue(ack);
        }

        private static void SetFilter(CD_ROM cdrom) {
            cdrom.DataController.Filter.fileNumber = cdrom.ParameterBuffer.Dequeue();
            cdrom.DataController.Filter.channelNumber = cdrom.ParameterBuffer.Dequeue();
            Response ack = new Response(new byte[] { cdrom.stat }, Delays.INT3_General, Flags.INT3, cdrom.State);
            cdrom.Responses.Enqueue(ack);
        }

        private static void SeekP(CD_ROM cdrom) {
            if (cdrom.ParameterBuffer.Count > 0) {
                Error(cdrom, Errors.InvalidNumberOfParameters);
                return;
            }
            cdrom.stat |= 0x2;    //Motor On
            Response ack = new Response(new byte[] { cdrom.stat }, Delays.INT3_General, Flags.INT3, CDROMState.Seeking);
            cdrom.Responses.Enqueue(ack);

            int position = ((cdrom.M * 60 * 75) + (cdrom.S * 75) + cdrom.F - 150) * 0x930;

            //You obviously, cannot seek beyond the entire disk 
            if (position > cdrom.DataController.EndOfDisk) {
                cdrom.stat = 0x6;
                Error(cdrom, Errors.InvalidParameter, (long)(33868800 * (650*0.001)));        //650ms
                return;
            }

            //Since SeekP is for audio mode we are ok with seeking outside the data track, actually, it may not be allowed to use SeekP in the data track

            int oldPosition = cdrom.CurrentIndex;
            cdrom.CurrentIndex = ((cdrom.M * 60 * 75) + (cdrom.S * 75) + cdrom.F - 150) * 0x930;

            Response done = new Response(new byte[] { cdrom.stat }, CalculateSeekTime((int)oldPosition, (int)cdrom.CurrentIndex), (int)Flags.INT2, CDROMState.Idle);
            cdrom.Responses.Enqueue(done);
            cdrom.SetLoc = false;
            cdrom.DataController.SelectTrack(cdrom.DataController.FindTrack(cdrom.CurrentIndex));
            cdrom.SeekedP = true;
        }

        private static void Play(CD_ROM cdrom) {   //CD-DA
            if (cdrom.State == CDROMState.ReadingData || cdrom.State == CDROMState.PlayingCDDA) {
                FlushINT1(cdrom);
            }

            int newIndex = ((cdrom.M * 60 * 75) + (cdrom.S * 75) + cdrom.F) * 0x930;        //No 2 sectors offset?
            cdrom.ReadRate = OneSecond / (cdrom.DoubleSpeed ? 150 : 75);

            if (cdrom.SetLoc) {
                Console.WriteLine("[CDROM] Play without Seek");
                cdrom.ReadRate += (int)CalculateSeekTime((int)cdrom.CurrentIndex, (int)newIndex);
                cdrom.SetLoc = false;
            }
            cdrom.CurrentIndex = newIndex;
            cdrom.stat |= 0x2;    //Motor On
            cdrom.SkipRate = 0;   //Reset skip rate
            Response ack = new Response(new byte[] { cdrom.stat }, Delays.INT3_General, Flags.INT3, CDROMState.PlayingCDDA);
            cdrom.Responses.Enqueue(ack);

            if (cdrom.ParameterBuffer.Count > 0 && cdrom.ParameterBuffer.Peek() > 0) {
                int trackNumber = cdrom.ParameterBuffer.Dequeue();
                if (cdrom.DataController.SelectedTrackNumber != trackNumber) {
                    cdrom.DataController.SelectTrack(trackNumber);      //Change Binary to trackNumber (if it isn't already selected)
                }

                //If a specific track is selected, start at the beginning of it
                cdrom.M = cdrom.DataController.Disk.Tracks[cdrom.DataController.SelectedTrackNumber - 1].M;
                cdrom.S = cdrom.DataController.Disk.Tracks[cdrom.DataController.SelectedTrackNumber - 1].S;
                cdrom.F = cdrom.DataController.Disk.Tracks[cdrom.DataController.SelectedTrackNumber - 1].F;
                cdrom.CurrentIndex = ((cdrom.M * 60 * 75) + (cdrom.S * 75) + cdrom.F) * 0x930;
            } else {
                //Find and select the right track
                int trackNumber = cdrom.DataController.FindTrack(cdrom.CurrentIndex);
                if (cdrom.DataController.SelectedTrackNumber != trackNumber) {
                    cdrom.DataController.SelectTrack(trackNumber);     
                }
            }

            //This is the first response of the play, it has to change the state and to have the playing cdda bit set to 1
            
            Response read = new Response(new byte[] { (byte)(cdrom.stat | (1 << 7)) }, cdrom.ReadRate, (int)Flags.Dummy, CDROMState.PlayingCDDA);
            cdrom.Responses.Enqueue(read);

            /*Console.WriteLine("[CDROM] CD-DA at MSF: " + cdrom.M + ":" + cdrom.S + ":" + cdrom.F +
                " - Track: " + cdrom.DataController.SelectedTrackNumber);*/
            cdrom.State = CDROMState.PlayingCDDA;
        }

        private static void Stop(CD_ROM cdrom) {
            //The first response returns the current status (this already with bit5 cleared)
            //The second response returns the new status (with bit1 cleared)
            //It moves the drive head to the begin of the first track

            cdrom.Responses.Clear();    //Very questionable
            Scheduler.FlushEvents(Event.CDROM_General);
            Scheduler.FlushEvents(Event.CDROM_ReadOrPlay);

            cdrom.stat = 0x2;
            Response ack = new Response(new byte[] { cdrom.stat }, Delays.INT3_General, Flags.INT3, CDROMState.Idle);
            cdrom.stat = 0x0;
            Response done = new Response(new byte[] { cdrom.stat }, cdrom.DoubleSpeed? Delays.INT2_Stop_DoubleSpeed : Delays.INT2_Stop_SingleSpeed, Flags.INT2, CDROMState.Idle);
            cdrom.Responses.Enqueue(ack);
            cdrom.Responses.Enqueue(done);

            if (cdrom.DataController.Disk != null && cdrom.DataController.Disk.IsValid) {
                cdrom.M = cdrom.DataController.Disk.Tracks[0].M;
                cdrom.S = cdrom.DataController.Disk.Tracks[0].S;
                cdrom.F = cdrom.DataController.Disk.Tracks[0].F;
                cdrom.DataController.SelectTrack(1);
            }

            //TODO: Timing for stop while stopped? stop while paused? 
            cdrom.State = CDROMState.Idle;
        }

        private static void GetTD(CD_ROM cdrom) {
            //GetTD - Command 14h,track --> INT3(stat,mm,ss) ;BCD
            /*For a disk with NN tracks, parameter values 01h..NNh return the start of the specified track, 
             *parameter value 00h returns the end of the last track, and parameter values bigger than NNh return error code 10h.*/
            if (cdrom.ParameterBuffer.Count != 1) {
                cdrom.ParameterBuffer.Clear();
                Error(cdrom, Errors.InvalidNumberOfParameters);
                return;
            }

            int BCD = cdrom.ParameterBuffer.Dequeue();
            int N = BcdToDec((byte)BCD);
            int lastIndex = cdrom.DataController.Disk.Tracks.Length - 1;
            int lastTrack = cdrom.DataController.Disk.Tracks[lastIndex].TrackNumber;

            if (N > lastTrack || !IsValidBCD((byte)BCD)) {
                Error(cdrom, Errors.InvalidParameter);
                return;
            }
            Response ack;
            if (N == 0) {
                (int M, int S, int F) = BytesToMSF(cdrom.DataController.Disk.Tracks[lastIndex].Length);
                M += cdrom.DataController.Disk.Tracks[lastIndex].M;
                S += (cdrom.DataController.Disk.Tracks[lastIndex].S + 2);
                ack = new Response(new byte[] { cdrom.stat, DecToBcd((byte)M), DecToBcd((byte)S) }, Delays.INT3_General, Flags.INT3, cdrom.State);
                cdrom.Responses.Enqueue(ack);
                Console.WriteLine("[CDROM] GETTD: Track 0, Response: " + M + " : " + S);
            } else {
                byte M = DecToBcd((byte)cdrom.DataController.Disk.Tracks[N - 1].M);
                byte S = DecToBcd((byte)(cdrom.DataController.Disk.Tracks[N - 1].S + 2));
                ack = new Response(new byte[] { cdrom.stat, M, S }, Delays.INT3_General, Flags.INT3, cdrom.State);
                cdrom.Responses.Enqueue(ack);
                Console.WriteLine("[CDROM] GETTD: Track " + N + ", Response: " + M + " : " + S);
            }
        }
        private static void GetTN(CD_ROM cdrom) {
            if (cdrom.ParameterBuffer.Count > 0) {
                cdrom.ParameterBuffer.Clear();
                Error(cdrom,Errors.InvalidNumberOfParameters);
                return;
            }
            //GetTN - Command 13h --> INT3(stat,first,last) ;BCD
            int lastIndex = cdrom.DataController.Disk.Tracks.Length - 1;
            byte firstTrack = DecToBcd((byte)cdrom.DataController.Disk.Tracks[0].TrackNumber);
            byte lastTrack = DecToBcd((byte)cdrom.DataController.Disk.Tracks[lastIndex].TrackNumber);
            Response ack = new Response(new byte[] { cdrom.stat, firstTrack, lastTrack}, Delays.INT3_General, Flags.INT3, cdrom.State);
            cdrom.Responses.Enqueue(ack);

            Console.WriteLine("[CDROM] GETTN, Response (BCD): " + firstTrack + " and " + lastTrack);
        }

        private static void Mute(CD_ROM cdrom) {
            Response ack = new Response(new byte[] { cdrom.stat }, Delays.INT3_General, Flags.INT3, cdrom.State);
            cdrom.Responses.Enqueue(ack);
        }

        private static void Demute(CD_ROM cdrom) {
            Response ack = new Response(new byte[] { cdrom.stat }, Delays.INT3_General, Flags.INT3, cdrom.State);
            cdrom.Responses.Enqueue(ack);
        }

        private static void Pause(CD_ROM cdrom) {
            cdrom.Responses.Clear();    //This is very questionable
            Scheduler.FlushEvents(Event.CDROM_ReadOrPlay);

            Response ack = new Response(new byte[] { cdrom.stat }, Delays.INT3_General, Flags.INT3, CDROMState.Idle);    //Stat is still reading
            cdrom.stat = 0x2;
            Response done = new Response(new byte[] { cdrom.stat }, cdrom.DoubleSpeed? Delays.INT2_Pause_DoubleSpeed : Delays.INT2_Pause_SingleSpeed, Flags.INT2, CDROMState.Idle);
            cdrom.Responses.Enqueue(ack);
            cdrom.Responses.Enqueue(done);
            //Todo: Pause while paused timings?

            cdrom.State = CDROMState.Idle;

        }

        private static void Init(CD_ROM cdrom) {
   
            if (cdrom.ParameterBuffer.Count > 0) {
                cdrom.ParameterBuffer.Clear();
                Error(cdrom, Errors.InvalidNumberOfParameters);
                Console.WriteLine("[CDROM] Init error, too many parameters");
                return;
            }

            //Multiple effects at once. Sets mode=00h (or not ALL bits cleared?), activates drive motor, Standby, abort all commands.
            cdrom.Mode = 0;         
            cdrom.stat = 0x2;
            cdrom.Responses.Clear();

            Scheduler.FlushEvents(Event.CDROM_General);
            Scheduler.FlushEvents(Event.CDROM_ReadOrPlay);

            cdrom.DataController.DataFifo.Clear();
            //cdrom.DataController.SectorBuffer.Clear();
            cdrom.DataController.SelectTrack(1);
            cdrom.M = 0x00;
            cdrom.S = 0x02;
            cdrom.F = 0x00;
            cdrom.SeekedL = cdrom.SeekedP = false;
            cdrom.Responses.Enqueue(new Response(new byte[] { cdrom.stat }, Delays.INT3_Init, Flags.INT3, CDROMState.Idle));
            cdrom.Responses.Enqueue(new Response(new byte[] { cdrom.stat }, Delays.INT2_Init, Flags.INT2, CDROMState.Idle));
            cdrom.State = CDROMState.Idle;
        }

        private static void ReadNS(CD_ROM cdrom) {
            if (cdrom.State == CDROMState.ReadingData || cdrom.State == CDROMState.PlayingCDDA) {
                FlushINT1(cdrom);
            }

            if (cdrom.DataController.Disk.IsAudioDisk) {
                if (!cdrom.ReadCDDASectors) {             //If Audio Disk and CDDA is disabled return error 0x40
                    Error(cdrom, Errors.InvalidCommand);
                    return;
                }
            }

            int newIndex = ((cdrom.M * 60 * 75) + (cdrom.S * 75) + cdrom.F - 150) * 0x930;
            cdrom.ReadRate = OneSecond / (cdrom.DoubleSpeed ? 150 : 75);

            if (cdrom.SetLoc) {
                //Console.WriteLine("[CDROM] Read without Seek");
                cdrom.ReadRate += CalculateSeekTime(cdrom.CurrentIndex, newIndex);
                cdrom.SetLoc = false;
            }
            cdrom.CurrentIndex = newIndex;
            cdrom.stat |= 0x2;    //Motor On
            Response ack = new Response(new byte[] { cdrom.stat }, Delays.INT3_General, Flags.INT3, CDROMState.ReadingData);
            cdrom.Responses.Enqueue(ack);

            if (cdrom.CurrentIndex > cdrom.DataController.Disk.Tracks[0].Length) {           //Error if reading out of data track
                int last = cdrom.DataController.Disk.Tracks.Length - 1;
                ack.NextState = CDROMState.Idle;                                             //Abort state transition
                cdrom.stat = 0x6;
                Errors error;
                int delay;
                if (cdrom.CurrentIndex > cdrom.DataController.EndOfDisk) {                   //Different error if reading out of the whole disk
                    error = Errors.InvalidParameter;
                     delay = (int)(OneSecond * 0.7);
                } else {
                    error = Errors.SeekError;
                    delay = (OneSecond * 4) + 300000;
                }
                Error(cdrom, error, delay);
                return; 
            }

            if (cdrom.DataController.SelectedTrackNumber != 1) {
                cdrom.DataController.SelectTrack(1);      //Change Binary to main (only if it isn't already on main)
            }

            //This is the first response of the read, it has to change the state and to have the reading bit set to 1
            Response read = new Response(new byte[] { (byte)(cdrom.stat | (1 << 5)) }, cdrom.ReadRate, (int)Flags.INT1, CDROMState.ReadingData);
            cdrom.Responses.Enqueue(read);
            cdrom.State = CDROMState.ReadingData;
        }

        private static void SetMode(CD_ROM cdrom) {
            if (cdrom.ParameterBuffer.Count != 1) {
                Error(cdrom, Errors.InvalidNumberOfParameters);
                return;
            }
            cdrom.Mode = cdrom.ParameterBuffer.Dequeue();

            if (((cdrom.Mode >> 4) & 1) == 0) {
                if (((cdrom.Mode >> 5) & 1) == 0) {
                    cdrom.LastSize = 0x800;
                    cdrom.SectorOffset = 24;
                } else {
                    cdrom.LastSize = 0x924;
                    cdrom.SectorOffset = 12;
                }
            }

            cdrom.DataController.BytesToSkip = cdrom.SectorOffset;
            cdrom.DataController.SizeOfDataSegment = cdrom.LastSize;
            cdrom.DataController.Filter.IsEnabled = ((cdrom.Mode >> 3) & 1) != 0;

            cdrom.DoubleSpeed = ((cdrom.Mode >> 7) & 1) != 0;
            cdrom.AutoPause = ((cdrom.Mode >> 1) & 1) != 0;  //For audio play only
            cdrom.CDDAReport = ((cdrom.Mode >> 2) & 1) != 0; //For audio play only
            cdrom.ReadCDDASectors = (cdrom.Mode & 1) != 0;  
            cdrom.DataController.XA_ADPCM_En = ((cdrom.Mode >> 6) & 1) != 0;    //(0=Off, 1=Send XA-ADPCM sectors to SPU Audio Input)

            cdrom.Responses.Enqueue(new Response(new byte[] { cdrom.stat }, Delays.INT3_General, Flags.INT3, cdrom.State));
        }

        private static void SeekL(CD_ROM cdrom) {
            if (cdrom.ParameterBuffer.Count > 0) {
                Error(cdrom, Errors.InvalidNumberOfParameters);
                return;
            }
            cdrom.stat |= 0x2;    //Motor On
            Response ack = new Response(new byte[] { cdrom.stat }, Delays.INT3_General, Flags.INT3, CDROMState.Seeking);
            cdrom.Responses.Enqueue(ack);

            int position = ((cdrom.M * 60 * 75) + (cdrom.S * 75) + cdrom.F - 150) * 0x930;

            //You obviously, cannot seek beyond the entire disk 
            if (position > cdrom.DataController.EndOfDisk) {
                cdrom.stat = 0x6;
                Error(cdrom, Errors.InvalidParameter, 33868800 * 1);        //1s
                Console.WriteLine("[CDROM] SeekL error, out of disk!");
                return;
            }
            //Since this is Seekl you cannot seek beyond the data track
            if (position > cdrom.DataController.Disk.Tracks[0].Length) {         
                cdrom.stat = 0x6;
                Error(cdrom, Errors.SeekError, (OneSecond * 4) + 300000);             //~4s
                return;
            }
            int oldPosition = cdrom.CurrentIndex;
            cdrom.CurrentIndex = ((cdrom.M * 60 * 75) + (cdrom.S * 75) + cdrom.F - 150) * 0x930;

            Response done = new Response(new byte[] { cdrom.stat }, CalculateSeekTime(oldPosition, cdrom.CurrentIndex), (int)Flags.INT2, CDROMState.Idle);
            cdrom.Responses.Enqueue(done);
            cdrom.SetLoc = false;
            cdrom.DataController.SelectTrack(cdrom.DataController.FindTrack(cdrom.CurrentIndex));
            cdrom.SeekedL = true;
        }

        private static void Setloc(CD_ROM cdrom) {
            if (cdrom.ParameterBuffer.Count != 3) {
                Error(cdrom, Errors.InvalidNumberOfParameters);
                return;
            }
            cdrom.SeekParameters[0] = cdrom.ParameterBuffer.Dequeue();  //Minutes
            cdrom.SeekParameters[1] = cdrom.ParameterBuffer.Dequeue();  //Seconds 
            cdrom.SeekParameters[2] = cdrom.ParameterBuffer.Dequeue();  //Sectors (Frames)


            int MM = ((cdrom.SeekParameters[0] & 0xF) * 1) + (((cdrom.SeekParameters[0] >> 4) & 0xF) * 10);
            int SS = ((cdrom.SeekParameters[1] & 0xF) * 1) + (((cdrom.SeekParameters[1] >> 4) & 0xF) * 10);
            int FF = ((cdrom.SeekParameters[2] & 0xF) * 1) + (((cdrom.SeekParameters[2] >> 4) & 0xF) * 10);

            if (IsValidBCD(cdrom.SeekParameters[0]) && IsValidBCD(cdrom.SeekParameters[1]) && IsValidBCD(cdrom.SeekParameters[2]) && IsValidSetloc(MM,SS,FF)) {
                cdrom.M = MM;
                cdrom.S = SS;
                cdrom.F = FF;
                cdrom.Responses.Enqueue(new Response(new byte[] { cdrom.stat }, Delays.INT3_General, Flags.INT3, cdrom.State));
            } else {
                Error(cdrom, Errors.InvalidParameter);
            }
            cdrom.SetLoc = true;

            Console.WriteLine("[CDROM] Setloc -> " + MM.ToString().PadLeft(2,'0') + ":" + 
                SS.ToString().PadLeft(2, '0') + ":" + FF.ToString().PadLeft(2, '0'));
        }

        private static void Error(CD_ROM cdrom, Errors code) {  //General Command Error
            int open = cdrom.LidOpen ? (1 << 4) : 0;
            CDROMState nextState = cdrom.LidOpen? CDROMState.SwappingDisk : CDROMState.Idle;
            cdrom.stat = (byte)(0x3 | open);   
            cdrom.Responses.Enqueue(new Response(new byte[] { cdrom.stat, (byte)code }, Delays.INT5, Flags.INT5, nextState));
            cdrom.stat = (byte)(0x2 | open);
        }

        private static void Error(CD_ROM cdrom, Errors code, long Delay) {   //Overload for custom delay
            int open = cdrom.LidOpen ? (1 << 4) : 0;
            CDROMState nextState = cdrom.LidOpen ? CDROMState.SwappingDisk : CDROMState.Idle;
            cdrom.Responses.Enqueue(new Response(new byte[] { (byte)(cdrom.stat | open), (byte)code }, Delay, (int)Flags.INT5, nextState));
            cdrom.stat = (byte)(0x2 | open);                
        }

        private static void GetID(CD_ROM cdrom) {
            if (cdrom.ParameterBuffer.Count > 0) {
                cdrom.ParameterBuffer.Clear();
                Error(cdrom, Errors.InvalidNumberOfParameters);
                return;
            }
            Response ack = new Response(new byte[] { cdrom.stat }, Delays.INT3_General, Flags.INT3, cdrom.State);
            cdrom.Responses.Enqueue(ack);
            Response done;

            if (cdrom.DataController.Disk != null && cdrom.DataController.Disk.IsValid) {
                if (cdrom.DataController.Disk.IsAudioDisk) {
                    done = new Response(new byte[] { 0x0A, 0x90, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }, Delays.INT5, Flags.INT5, cdrom.State);
                } else {
                    done = new Response(new byte[] { 0x02, 0x00, 0x20, 0x00, 0x53, 0x43, 0x45, 0x41 }, Delays.INT2_GetID, Flags.INT2, cdrom.State);
                }
            } else {
                //No Disk -> INT3(stat) -> INT5(08h,40h, 00h,00h)
                done = new Response(new byte[] { 0x08, 0x40, 0x00, 0x00 }, Delays.INT5, Flags.INT5, cdrom.State);
            }
            cdrom.Responses.Enqueue(done);
        }
        private static void GetStat(CD_ROM cdrom) {
            if (!cdrom.LidOpen) {     //Reset shell open unless it is still opened, TODO: add disk swap
                cdrom.stat = (byte)(cdrom.stat & (~0x18));
                cdrom.stat |= 0x2;
            }
            if (cdrom.ParameterBuffer.Count > 0) {
                cdrom.ParameterBuffer.Clear();
                Error(cdrom, Errors.InvalidNumberOfParameters);
                cdrom.stat = 0x2;
                return;
            }
            Response ack = new Response(new byte[] { cdrom.stat }, Delays.INT3_General, Flags.INT3, cdrom.State);
            cdrom.Responses.Enqueue(ack);
        }

        private static void GetDateAndVersion(CD_ROM cdrom) {  //Returns 0x94, 0x09, 0x19, 0xC0, but we can set anything
            if (cdrom.ParameterBuffer.Count > 0) {
                Error(cdrom,Errors.InvalidNumberOfParameters);
                return;
            }
            Response ack = new Response(new byte[] { 0x94, 0x09, 0x19, 0xC0 }, Delays.INT3_General, Flags.INT3, cdrom.State);
            cdrom.Responses.Enqueue(ack);
        }

        private byte[] GetCDDAReport() {  //TODO
            //Report --> INT1(stat,track,index,mm/amm,ss+80h/ass,sect/asect,peaklo,peakhi)
            bool isEven = ((F / 10) & 1) == 0;
            int track = DataController.SelectedTrackNumber;
            int mm;
            int ss;
            int ff;
            int index = 0x01; //TODO handle multi index tracks
            bool pregap = CurrentIndex < DataController.Disk.Tracks[track - 1].Start;   
            int or = 0;
            if (isEven) {   //Absolute
                mm = M;
                ss = S;
                ff = F;

            } else {      //Relative
                int trackStart = DataController.Disk.Tracks[track - 1].Start;
                int difference = (int)(pregap? trackStart  - CurrentIndex : CurrentIndex - trackStart); 
                (mm, ss, ff) = BytesToMSF(difference);
                or = 0x80;
            }
            return new byte[] { stat, DecToBcd((byte)track), DecToBcd((byte)index) , DecToBcd((byte)mm), (byte)(DecToBcd((byte)ss) | or), DecToBcd((byte)ff), 0x00, 0xFF };
        }

        void IncrementIndex(byte offset) {
            F = (F + 1 + SkipRate);
            if (F >= 75) { S = S + (F / 75); F = F % 75; }
            if (S >= 60) { M = M + (S / 60); S = S % 60; }
            CurrentIndex = ((M * 60 * 75) + (S * 75) + F - offset) * 0x930;
            SeekedP = SeekedL = false;

            //Backward automatically switches to Play when reaching the begin of Track 1.
            if (SkipRate < 0 && CurrentIndex < DataController.Disk.Tracks[0].Start) {
                SkipRate = 0;
            }

            //Forward automatically Stops the drive motor with INT4(stat) when reaching the end of the last track.
            if (SkipRate > 0 && CurrentIndex > DataController.EndOfDisk) {
                SkipRate = 0;
                stat = 0;
                Responses.Enqueue(new Response(new byte[] { stat }, Delays.INT3_General, Flags.INT4, CDROMState.Idle));
                Console.WriteLine("[CDROM] Reached end of disk!");
            }
        }

        private void Step(int cycles) {
            int count = Responses.Count;
            for (int i = 0; i < count; i++) {
                Responses.ElementAt(i).delay -= cycles;
                if (Responses.ElementAt(i).delay >= 0) {
                    break;
                } else {
                    //Responses.ElementAt(i).delay = (int)Math.Ceiling(33868800 * 0.0006);   //600us
                    Responses.ElementAt(i).FinishedProcessing = true;
                }
            }
        }

        public void tick(int cycles) {
            Step(cycles);
            
            if (Responses.Count > 0) {
                if ((IRQ_Flag & 0x7) == 0 && Responses.Peek().delay <= 0) {    //Make sure the previous INT has been ACKed, INTs are queued not ORed
                    Response current = Responses.Dequeue();
                    State = current.NextState;
                    IRQ_Flag |= (byte)current.interrupt;
                    ResponseBuffer.WriteBuffer(ref current.values);
                    if ((IRQ_Enable & IRQ_Flag) != 0) {
                        IRQ_CONTROL.IRQsignal(2);
                    }
                }
            }
            if (TransmissionDelay > 0) {
                TransmissionDelay -= cycles;
            }
            switch (State) {
                case CDROMState.Idle:
                    stat = 0x2; 
                    return;

                case CDROMState.SwappingDisk:
                    if (SwappingDelay > 0) {
                        SwappingDelay -= cycles;
                        stat |= (1 << 4);  //Lid open bit
                        LidOpen = true;
                    } else {
                        State = CDROMState.Idle;
                        LidOpen = false;
                    }
                    return;

                case CDROMState.Seeking:
                    stat |= (1 << 6);
                    return;

                case CDROMState.ReadingData:
                    if ((ReadRate -= cycles) > 0) {
                        return;
                    }

                    if (CurrentIndex >= DataController.EndOfDisk) {
                        Response pause = new Response(new byte[] { stat }, Delays.Zero, Flags.INT4, CDROMState.Idle);
                        Responses.Enqueue(pause);
                        Console.WriteLine("[CDROM] End of Disk!");
                        return;
                    }

                    stat |= (1 << 5);   //Read at least one sector before setting the bit

                    Console.WriteLine("[CDROM] Data Read at " + M.ToString().PadLeft(2,'0') + ":" + S.ToString().PadLeft(2, '0') + ":" + F.ToString().PadLeft(2, '0')
                        + " --- Index :" + CurrentIndex.ToString("x"));

                    bool sendToCPU = DataController.LoadNewSector(CurrentIndex);
                    IncrementIndex(150);
                    if (sendToCPU) {
                        Response sectorAck = new Response(new byte[] { stat }, Delays.Zero, Flags.INT1, CDROMState.ReadingData);    //Zero, it was already waiting
                        Responses.Enqueue(sectorAck);
                    }
                    ReadRate = OneSecond / (DoubleSpeed ? 150 : 75);    //Update the rate for next INTs

                    break;

                case CDROMState.PlayingCDDA:
                    if ((ReadRate -= cycles) > 0) {
                        return;
                    }

                    if (CurrentIndex >= DataController.EndOfDisk || (CurrentIndex >= DataController.EndOfTrack && AutoPause)) {
                        Response pause = new Response(new byte[] { stat }, Delays.Zero, Flags.INT4, CDROMState.Idle);
                        Responses.Enqueue(pause);
                        Console.WriteLine("[CDROM] CD-DA Paused Track: " + DataController.SelectedTrackNumber);
                        return;
                    }

                    //Console.WriteLine("[CDROM] Play at MSF: " + M.ToString().PadLeft(2,'0') + ":" + S.ToString().PadLeft(2, '0') + ":" + F.ToString().PadLeft(2, '0'));

                    stat |= (1 << 7);   //Play at least one sector before setting the bit

                    if (DataController.Disk.HasCue) {
                        DataController.PlayCDDA(CurrentIndex);
                    } else {
                        Console.WriteLine("[CD-ROM] Ignoring play command (No cue)");
                    }

                    if (CDDAReport && IsReportableSector) {  
                        Response report = new Response(GetCDDAReport(), Delays.Zero, Flags.INT1, CDROMState.PlayingCDDA);
                        Responses.Enqueue(report);
                    }

                    IncrementIndex(0);              
                    ReadRate = OneSecond / (DoubleSpeed ? 150 : 75);    //Update the rate for next INTs
                    break;
            }
        }

        public void CDROMEventHandler() {
            bool consumed = false;
            bool shouldReadOrPlay = false;
            Response next;
            ReadRate = OneSecond / (DoubleSpeed ? 150 : 75);

            if (Responses.Count > 0) {
                if ((IRQ_Flag & 0x7) == 0) {                                   //Make sure the previous INT has been ACKed, INTs are queued not ORed
                    Response current = Responses.Dequeue();
                    shouldReadOrPlay = current.interrupt == (int)Flags.INT1 || current.interrupt == (int)Flags.Dummy;
                    if (current.interrupt != (int)Flags.Dummy) {               //Dummy responses are not actual responses
                        State = current.NextState;                             //But we need them to continue reading/playing if there is no actual response
                        IRQ_Flag |= (byte)current.interrupt;
                        ResponseBuffer.WriteBuffer(ref current.values);
                        if ((IRQ_Enable & IRQ_Flag) != 0) {
                            IRQ_CONTROL.IRQsignal(2);
                        }
                    }
                    Console.WriteLine($"Response INT {current.interrupt} State: {State}");
                    consumed = true;
                }
            }
            
            switch (State) {
                case CDROMState.Idle:
                    stat = 0x2;
                    break;

                case CDROMState.SwappingDisk:
                    if (SwappingDelay > 0) {        //TODO
                       // SwappingDelay -= cycles;
                        stat |= (1 << 4);  //Lid open bit
                        LidOpen = true;
                    } else {
                        State = CDROMState.Idle;
                        LidOpen = false;
                    }
                    break;

                case CDROMState.Seeking:
                    stat |= (1 << 6);
                    break;

                case CDROMState.ReadingData:
                    if (!shouldReadOrPlay) { break; }

                    if (CurrentIndex >= DataController.EndOfDisk) {
                        Response pause = new Response(new byte[] { stat }, Delays.Zero, Flags.INT4, CDROMState.Idle);
                        Responses.Enqueue(pause);
                        Console.WriteLine("[CDROM] End of Disk!");
                        break;
                    }

                    //Ensure the reading flag is set
                    stat |= (1 << 5);

                    /*Console.WriteLine("[CDROM] Data Read at " + M.ToString().PadLeft(2,'0') + ":" 
                        + S.ToString().PadLeft(2, '0') + ":" + F.ToString().PadLeft(2, '0')
                        + " --- Index :" + CurrentIndex.ToString("x"));*/

                    bool sendToCPU = DataController.LoadNewSector(CurrentIndex);
                    IncrementIndex(150);

                    if (sendToCPU) {
                        next = new Response(new byte[] { stat }, ReadRate, (int)Flags.INT1, State);  
                    } else {
                        next = new Response(new byte[] { stat }, ReadRate, (int)Flags.Dummy, State);    
                    }

                    Responses.Enqueue(next);
                    break;

                case CDROMState.PlayingCDDA:
                    if (!shouldReadOrPlay) { break; }

                    if (CurrentIndex >= DataController.EndOfDisk || (CurrentIndex >= DataController.EndOfTrack && AutoPause)) {
                        Response pause = new Response(new byte[] { stat }, Delays.Zero, Flags.INT4, CDROMState.Idle);
                        Responses.Enqueue(pause);
                        Console.WriteLine("[CDROM] CD-DA Paused Track: " + DataController.SelectedTrackNumber);
                        break;
                    }

                    Console.WriteLine("[CDROM] Play at MSF: " + M.ToString().PadLeft(2,'0') + ":" + S.ToString().PadLeft(2, '0') + ":" + F.ToString().PadLeft(2, '0'));

                    //Ensure the playing cdda flag is set
                    stat |= (1 << 7);  

                    if (DataController.Disk.HasCue) {
                        DataController.PlayCDDA(CurrentIndex);
                    } else {
                        Console.WriteLine("[CD-ROM] Ignoring play command (No cue)");
                    }

                    if (CDDAReport && IsReportableSector) {
                        next = new Response(GetCDDAReport(), ReadRate, (int)Flags.INT1, State);
                    } else {
                        next = new Response(new byte[] { stat }, ReadRate, (int)Flags.Dummy, State);
                    }

                    Responses.Enqueue(next);
                    IncrementIndex(0);
                    break;
            }

            if (!consumed) {        
                Console.WriteLine($"CD Response not consumed: {Responses.Peek().interrupt}");   //This should be very rare
            }

            ScheduleCDResponse();
        }

        private bool ShouldReadOrPlay(Response current) {
            bool isReading = State == CDROMState.ReadingData;
            bool isPlaying = State == CDROMState.PlayingCDDA;
            bool isINT1 = current.interrupt == (int)Flags.INT1;
            bool isDummy = current.interrupt == (int)Flags.Dummy;
            return (isReading || isPlaying) && (isINT1 || isDummy);
        }

        private void ResponseHandler() {
            ReadRate = OneSecond / (DoubleSpeed ? 150 : 75);

            bool consumed = false;
            bool shouldReadOrPlay = false;

            if (Responses.Count > 0) {
                if ((IRQ_Flag & 0x7) == 0) {                                   //Make sure the previous INT has been ACKed, INTs are queued not ORed
                    Response current = Responses.Dequeue();
                    //State = current.NextState;

                    shouldReadOrPlay = ShouldReadOrPlay(current);

                    if (current.interrupt != (int)Flags.Dummy) {               //Dummy responses are not actual responses                                                 
                        IRQ_Flag |= (byte)current.interrupt;                   //But we need them to continue reading/playing if there is no actual response
                        ResponseBuffer.WriteBuffer(ref current.values);
                        if ((IRQ_Enable & IRQ_Flag) != 0) {
                            IRQ_CONTROL.IRQsignal(2);
                        }
                    }

                    //Console.WriteLine($"Response INT {current.interrupt} State: {State}");
                    consumed = true;
                }
            }

            switch (State) {
                case CDROMState.Idle: stat = 0x2; break;
                case CDROMState.Seeking: stat |= (1 << 6); break;
                case CDROMState.ReadingData: if (shouldReadOrPlay) { ReadHandler(); } break;
                case CDROMState.PlayingCDDA: if (shouldReadOrPlay) { CDDAHandler(); } break;
            }

            //This should be very rare
            if (!consumed) {
                Console.WriteLine($"CD Response not consumed: {Responses.Peek().interrupt}");  
            }

            ScheduleCDResponse();
        }

        private void ReadHandler() {
            Response next;
            if (CurrentIndex >= DataController.EndOfDisk) {
                Response pause = new Response([stat], Delays.Zero, Flags.INT4, CDROMState.Idle);
                Responses.Enqueue(pause);
                Console.WriteLine("[CDROM] End of Disk!");
            }

            //Ensure the reading flag is set
            stat |= (1 << 5);

            //LogRead();

            bool sendToCPU = DataController.LoadNewSector(CurrentIndex);
            IncrementIndex(150);

            next = new Response([stat], ReadRate, sendToCPU ? (int)Flags.INT1 : (int)Flags.Dummy, State);
            Responses.Enqueue(next);
        }

        private void CDDAHandler() {
            Response next;
            if (CurrentIndex >= DataController.EndOfDisk || (CurrentIndex >= DataController.EndOfTrack && AutoPause)) {
                Response pause = new Response(new byte[] { stat }, Delays.Zero, Flags.INT4, CDROMState.Idle);
                Responses.Enqueue(pause);
                Console.WriteLine($"[CDROM] CD-DA Paused Track: {DataController.SelectedTrackNumber}");
            }
         
            //Ensure the playing cdda flag is set
            stat |= (1 << 7);

            //LogPlay();

            if (DataController.Disk.HasCue) {
                DataController.PlayCDDA(CurrentIndex);
            } else {
                Console.WriteLine("[CD-ROM] Ignoring play command (No cue)");
            }

            if (CDDAReport && IsReportableSector) {
                byte[] report = GetCDDAReport();
                next = new Response(report, ReadRate, (int)Flags.INT1, State);
            } else {
                next = new Response([stat], ReadRate, (int)Flags.Dummy, State);
            }

            Responses.Enqueue(next);
            IncrementIndex(0);
        }

        private void ScheduleCDResponse() {
            //We can Schedule one reading/playing + one general, but not 2 of the same kind
            if (Responses.Count > 0) {
                Response first = Responses.ElementAt(0);
                bool canSchedule;
                Event type;

                (canSchedule, type) = CanSchedule(first);
                if (canSchedule) {
                    Scheduler.ScheduleEvent((int)first.delay, CDROMCallback, type);
                }

                if (Responses.Count > 1) {
                    Response second = Responses.ElementAt(1);
                    (canSchedule, type) = CanSchedule(second);
                    if (canSchedule) {
                        Scheduler.ScheduleEvent((int)second.delay, CDROMCallback, type);
                        PossibleSwap(first, second);
                    }
                }

                if (Responses.Count > 3) {
                    Console.WriteLine($"[CDROM] Warning: Responses count = {Responses.Count}");
                }

                //Console.WriteLine($"Number of general: {Scheduler.HowManyEventOfType(Event.CDROM_General)}");
                //Console.WriteLine($"Number of Read/Play: {Scheduler.HowManyEventOfType(Event.CDROM_ReadOrPlay)}");

                //Console.WriteLine($"Scheduled INT {first.interrupt}");

                /*Console.Write("Queue content: ");
                foreach (var response in Responses) {
                    Console.Write($" {response.interrupt} ");
                }
                Console.WriteLine();*/
            }
        }

        private (bool, Event) CanSchedule(Response response) {
            Event typeOfEvent;

            if (response.interrupt == (int)(Flags.INT1) || response.interrupt == (int)(Flags.Dummy)) {
                typeOfEvent = Event.CDROM_ReadOrPlay;
            } else {
                typeOfEvent = Event.CDROM_General;
            }

            return (!Scheduler.HasEventOfType(typeOfEvent), typeOfEvent);
        }

        private void PossibleSwap(Response first, Response second) {
            if (second.delay < first.delay) {
                Responses.Dequeue();
                Responses.Dequeue();
                Responses.Enqueue(second);
                Responses.Enqueue(first);
            }
        }

        private static void FlushINT1(CD_ROM cdrom) {
            Scheduler.HasEventOfType(Event.CDROM_ReadOrPlay);

            //There shouldn't be more than one, it's either the first or the second
            Response response;
            if (cdrom.Responses.Count > 0) {
                response = cdrom.Responses.ElementAt(0);
                if (response.interrupt == (int)(Flags.INT1) || response.interrupt == (int)(Flags.Dummy)) {
                    cdrom.Responses.Dequeue();
                    return;
                }
            }

            if (cdrom.Responses.Count > 1) {
                response = cdrom.Responses.ElementAt(1);
                if (response.interrupt == (int)(Flags.INT1) || response.interrupt == (int)(Flags.Dummy)) {
                    Response temp = cdrom.Responses.Dequeue();
                    cdrom.Responses.Dequeue();
                    cdrom.Responses.Prepend(temp);
                    return;
                }
            }
        }

        private void LogRead() {
            Console.WriteLine($"[CDROM] Read at: {M:00}:{S:00}:{F:00}");
        }

        private void LogPlay() {
            Console.WriteLine($"[CDROM] Play at: {M:00}:{S:00}:{F:00}");
        }

        private static (int, int, int) BytesToMSF(int totalSize) {
            int totalFrames = totalSize / 2352;
            int M = totalFrames / (60 * 75);
            int S = (totalFrames % (60 * 75)) / 75;
            int F = (totalFrames % (60 * 75)) % 75;
            return (M, S, F);
        }

        private static byte DecToBcd(byte value) {
            return (byte)(value + 6 * (value / 10));
        }

        private static int BcdToDec(byte value) {
            return value - 6 * (value >> 4);
        }

        private static bool IsValidBCD(byte value) {
            return ((value & 0xF) < 0xA) && (((value >> 4) & 0xF) < 0xA);
        }

        private static bool IsValidSetloc(int M, int S, int F) {
            return M <= 99 && S <= 59 && F <= 74;
        }

        private static long CalculateSeekTime(int position, int destination) {
            long wait = (long)((long)OneSecond * 2 * 0.001 * (Math.Abs((position - destination)) / (75 * 0x930)));
            //Console.WriteLine("Difference of: " + (Math.Abs((position - destination)) / (75 * 0x930)) + " Seconds");
            //Console.WriteLine("Seek time: " + wait);
            return (long)Delays.INT3_General;  //Returning INT2_Init seems to work for Driver. At least to reach the menu
        }
    }
}
