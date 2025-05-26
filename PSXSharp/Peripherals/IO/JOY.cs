using PSXSharp.Core;
using PSXSharp.Core.x64_Recompiler;
using System;
using System.IO;

namespace PSXSharp.Peripherals.IO {
    public class JOY {  //SIO0                

        public Range Range = new Range(0x1F801040, 16);


        //The flags and timings of this implementation are not accurate
        //But it works, so...

        //Data (R/W) -> 1/4 word    (This is actually a FIFO)
        public byte TX_Data;
        public byte RX_Data;

        //Status (R) -> 4           This register comes after 4 bytes of Data address, although the Data length is only 1 byte
        struct StatusRegister {
            public byte TX_FiFO_Not_Full;    //0
            public byte RX_FIFO_Not_Empty;   //1
            public byte TX_Idle;             //2
            public byte RX_Parity_Error;     //3            
            public byte AckLevel;            //7
            public byte InterruptRequest;    //9
            public int Baud;                 //11 - 31
            //Bits 4,5,6,8,10 are unknown (zero) for JOY (SIO0)
        }

        //Mode (R/W) -> 2
         struct ModeRegister {
            public byte BaudrateReloadFactor;//0 - 1
            public byte CharacterLength;     //2 - 3
            public byte ParityEnable;        //4
            public byte ParityType;          //5
            public byte ClockPolarity;       //8         
            //Bits 6,7, and 9 - 15 are unknown for JOY (SIO0)
        }

        //Control (R/W) -> 2
         struct CtrlRegister {
            public byte TX_Enable;            //0
            public byte JOYnOutput;           //1
            public byte RX_Enable;            //2
            public byte Acknowledge;          //4     
            public byte Reset;                //6     
            public byte RX_InterruptMode;     //8 - 9
            public byte TX_InterruptEnable;   //10
            public byte RX_InterruptEnable;   //11
            public byte ACK_InterruptEnable;  //12
            public byte SelectedSlot;         //13   
            //Not used (zero)                 //14 - 15
            //Bits 3,5,7 are unknown for JOY (SIO0)
        }

        enum SelectedDevice {
            Controller, MemoryCard, None
        }

        enum AccessType {
            Controller = 0x01, 
            MemoryCard = 0x81
        }

        StatusRegister Status;
        CtrlRegister Ctrl;
        ModeRegister Mode;

        SelectedDevice selectedDevice = SelectedDevice.None;
        public Controller Controller1;
        public MemoryCard MemoryCard1;
        const int MEMORY_CARD_SIZE = 128 * 1024;
        int Delay = -1;
        Action Callback;
        int EventDelay = 1500;

        public JOY() {
            byte[] memoryCardData;

            try {
                memoryCardData = File.ReadAllBytes("MemoryCard.mcd");

            } catch (FileNotFoundException e) {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Could not find memory card, will create a new one.");
                Console.ForegroundColor = ConsoleColor.Green;
                memoryCardData = new byte[MEMORY_CARD_SIZE];
                File.WriteAllBytes("MemoryCard.mcd", memoryCardData);
            }

            MemoryCard1 = new MemoryCard(memoryCardData);
            Controller1 = new Controller();
            Callback = JOYEvent;
        }

        public uint LoadWord(uint address) {
            switch (address & 0xF) {
                case 0x0: return (uint)(RX_Data & 0xFF);
                case 0x4: return GetStatus();
                default: throw new Exception("Attempting to load word from: " + address.ToString("x"));
            }
        }

        public ushort LoadHalf(uint address) {
            //case 0x4?
            switch (address & 0xF) {
                case 0x0: return (ushort)(RX_Data & 0xFF);
                case 0x4: return (ushort)(GetStatus() & 0xFFFF);
                case 0x8: return GetMode();
                case 0xA: return GetCtrl();
                case 0xE: return (ushort)(Status.Baud & 0xFFFF);
                default: throw new Exception("Attempting to load half from: " + address.ToString("x"));
            }
        }

        public void StoreHalf(uint address, ushort data) {
            switch (address & 0xF) {
                case 0x0: Transfare((byte)data); break;
                case 0x8: WriteMode(data); break;
                case 0xA: WriteCtrl(data); break;
                case 0xE: 
                    Status.Baud = data;     //Boud rate is not implemented
                    EventDelay = data * 8;
                    break;
                default: throw new Exception("Attempting to store half to: " + address.ToString("x"));
            }
        }

        public byte LoadByte(uint address) {
            switch (address & 0xF) {
                case 0x00:
                    Status.RX_FIFO_Not_Empty = 0;
                    Status.TX_FiFO_Not_Full = 1;

                    if (Ctrl.SelectedSlot == 1) {     // Controller 2 and Memory card 2 are not connected
                        Status.AckLevel = 0;
                        return 0xFF;
                    } else {
                        //Console.WriteLine("RX: " + RX_Data.ToString("x"));
                        return RX_Data;
                    }

                case 0x04: return (byte)GetStatus();
                default: throw new Exception("[JOY] Unhandled reading from: " + address.ToString("x"));
            }
        }

        public void StoreByte(uint address, byte data) {
            StoreHalf(address, data); //Could be very wrong
        }

        public void JOYEvent() {
            Status.InterruptRequest = 1;
            Status.TX_Idle = 1;
            IRQ_CONTROL.IRQsignal(7);
            //Console.WriteLine("IRQ7");
        }

        public void Transfare(byte value) {
 
            RX_Data = 0xFF;
            TX_Data = value;
            Status.RX_FIFO_Not_Empty = 1;   //This sould probably have a delay.
            Status.TX_FiFO_Not_Full = 0;

            //Console.WriteLine("TX: " + TX_Data.ToString("x"));
            //Console.WriteLine("FOR: " + selectedDevice);
            //Console.WriteLine("Slot: " + Ctrl.SelectedSlot);

            if (Ctrl.SelectedSlot == 0) {
                switch (selectedDevice) {
                    case SelectedDevice.Controller:
                        RX_Data = Controller1.Response(TX_Data);
                        Status.AckLevel = (byte)(Controller1.ACK ? 1 : 0);
                        break;

                    case SelectedDevice.MemoryCard:
                        RX_Data = MemoryCard1.Response(TX_Data);
                        Status.AckLevel = (byte)(MemoryCard1.ACK ? 1 : 0);
                        break;

                    default:
                        //Handle First byte (selecting Controller vs Memorycard)

                        switch (TX_Data) {          
                            case (byte)AccessType.Controller: 
                                selectedDevice = SelectedDevice.Controller; 
                                //Console.WriteLine("ACCESS CONTROLLER");
                                break; 

                            case (byte)AccessType.MemoryCard: 
                                selectedDevice = SelectedDevice.MemoryCard;
                                //Console.WriteLine("ACCESS MEMCARD");
                                break; 

                            default:
                                Console.WriteLine("[JOY] Unknown device selected: " + TX_Data.ToString("X"));
                                Status.AckLevel = 0;
                                return;
                        }

                        Status.AckLevel = 1;
                        break;
                }

            } else {
                Status.AckLevel = 0;
            }

            if (Status.AckLevel == 0) {
                Controller1.SequenceNum = 0;
                MemoryCard1.Reset();
                Delay = -1;
                selectedDevice = SelectedDevice.None;
            } else {
                Scheduler.ScheduleEvent(EventDelay, Callback, Event.SIO);
            }
        }

        private uint GetStatus() {

            uint stat = 0;
            stat |= (byte)(Status.TX_FiFO_Not_Full & 1);
            stat |= (byte)((Status.RX_FIFO_Not_Empty & 1) << 1);
            stat |= (byte)((Status.TX_Idle & 1) << 2);
            stat |= (byte)((Status.RX_Parity_Error & 1) << 3);
            stat |= (byte)((Status.AckLevel & 1) << 7);
            stat |= (byte)((Status.InterruptRequest & 1) << 9);
            stat |= (ushort)((Status.Baud & 0xFFFF) << 11);

            Status.AckLevel = 0;
            //Console.WriteLine($"Read stat = {stat.ToString("x")}");

            return stat;
        }

        private ushort GetMode() {
            ushort mode = 0;
            mode |= (byte)(Mode.BaudrateReloadFactor & 3);
            mode |= (byte)((Mode.CharacterLength & 3) << 2);
            mode |= (byte)((Mode.ParityEnable & 1) << 4);
            mode |= (byte)((Mode.ParityType & 1) << 5);
            mode |= (byte)((Mode.ClockPolarity & 1) << 8);
            return mode;
        }

        private void WriteMode(ushort value) {
            Mode.BaudrateReloadFactor = (byte)(value & 3);
            Mode.CharacterLength = (byte)((value >> 2) & 3);
            Mode.ParityEnable = (byte)((value >> 4) & 1);
            Mode.ParityType = (byte)((value >> 5) & 1);
            Mode.ClockPolarity = (byte)((value >> 8) & 1);
        }

        private ushort GetCtrl() {
            ushort control = 0;
            control |= (byte)(Ctrl.TX_Enable & 1);
            control |= (byte)((Ctrl.JOYnOutput & 1) << 1);
            control |= (byte)((Ctrl.RX_Enable & 1) << 2);
            //4 is write only
            //6 is write only
            control |= (byte)((Ctrl.RX_InterruptMode & 3) << 8);
            control |= (byte)((Ctrl.TX_InterruptEnable & 1) << 10);
            control |= (byte)((Ctrl.RX_InterruptEnable & 1) << 11);
            control |= (byte)((Ctrl.ACK_InterruptEnable & 1) << 12);
            control |= (byte)((Ctrl.SelectedSlot & 1) << 13);

            //Console.WriteLine($"Read ctrl = {control.ToString("x")} , time = {currentTime.ToString("x")}");
            return control;
        }

        private void WriteCtrl(ushort control) {
            Ctrl.TX_Enable = (byte)(control & 1);
            Ctrl.JOYnOutput = (byte)((control >> 1) & 1);
            Ctrl.RX_Enable = (byte)((control >> 2) & 1);
            Ctrl.Acknowledge = (byte)((control >> 4) & 1);       //Write only
            Ctrl.Reset = (byte)((control >> 6) & 1);             //Write only
            Ctrl.RX_InterruptMode = (byte)((control >> 8) & 3);
            Ctrl.TX_InterruptEnable = (byte)((control >> 10) & 1);
            Ctrl.RX_InterruptEnable = (byte)((control >> 11) & 1);
            Ctrl.ACK_InterruptEnable = (byte)((control >> 12) & 1);
            Ctrl.SelectedSlot = (byte)((control >> 13) & 1);

            if (Ctrl.Acknowledge == 1) {
                Ctrl.Acknowledge = 0;
                Status.InterruptRequest = 0;
                Status.RX_Parity_Error = 0;
            }

            if (Ctrl.Reset == 1) {
                Ctrl.Reset = 0;
                MemoryCard1.Reset();
                selectedDevice = SelectedDevice.None;
                Controller1.SequenceNum = 0;
                RX_Data = 0xFF;
                TX_Data = 0xFF;
                Status.TX_FiFO_Not_Full = 1;
                Status.TX_Idle = 0;
                Scheduler.FlushEvents(Event.SIO);
                //Console.WriteLine($"Reset!");
            }

            //Console.WriteLine($"write ctrl = {control.ToString("x")}");
        }
    }
}
