using OpenTK.Windowing.GraphicsLibraryFramework;
using System;
using System.Collections.Generic;

namespace PSXSharp {
    public class Controller {
        public bool ACK;
        public bool IsConnected;
        public bool IgnoreInput;
        public ushort Buttons = 0xFFFF; 
        public byte RightJoyX = 0x80;  
        public byte RightJoyY = 0x80;
        public byte LeftJoyX = 0x80;
        public byte LeftJoyY = 0x80;
        public byte LEDStatus = 0x1;
        public byte LEDStatus_Temp = 0x0;
        public bool IsAnalog = false;

        bool SwitchModeNext = false;

        public int SequenceNum;

        public static readonly Dictionary<int, int> DualSenseDictionary = new Dictionary<int, int>() {
           {0, 15},      //Square
           {1, 14},      //X
           {2, 13},      //Circle
           {3, 12},      //Triangle
           {4, 10},      //L1
           {5, 11},      //R1
           {6, 8},       //L2
           {7, 9},       //R2
           {8, 0},       //Select
           {9, 3},       //Start
           {10, 1},      //L3
           {11, 2},      //R3
           {15, 4},      //Pad up
           {16, 5},      //Pad right
           {17, 6},      //Pad down
           {18, 7},      //Pad Left
        };
        public enum Mode { NormalMode, ConfigMode  }
        Mode ControllerMode = Mode.NormalMode;
        public uint CurrentCommand; //Mainly for Config
        byte[] VariableResponseA;
        byte VariableResponseB;
        byte[] RumbleConfiguration = new byte[] { 0x00, 0x01, 0xFF, 0xFF, 0xFF, 0xFF };    //Most games will use 00h 01h FFh FFh FFh FFh

        public byte Response(uint data) {

            if (!IsConnected) {
                ACK = false;
                return 0xFF;
            }

            ACK = true;
            byte ans = 0;
            if (ControllerMode == Mode.NormalMode) {
                ans = NormalModeResponse(data);
            } else {
                ans = ConfigModeResponse(data);
            }
           // Console.WriteLine("[PADs] Data: " + data.ToString("X") + " -- Seq:" + (sequenceNum - 1) + " -- Ans: " + ans.ToString("X") + " --- Mode: " + ControllerMode);
            return ans;
        }
        public byte NormalModeResponse(uint data) {
            switch (SequenceNum++) {
                case 0:
                    CurrentCommand = data;
                    return (byte)(IsAnalog ? 0x73 : 0x41);
                case 1: return 0x5A;
                case 2:
                    if (CurrentCommand == 0x43) {
                        if (!IsAnalog) {
                            SequenceNum = 0;
                            ACK = false;
                            return 0xFF;
                        }

                        if (data == 0x1) {
                            SwitchModeNext = true;
                        }

                    } else {
                        SwitchModeNext = false;
                    }

                    return (byte)(Buttons & 0xff);
                case 3:
                    if (!IsAnalog) {
                        SequenceNum = 0;
                        ACK = false;
                    }
                    return (byte)((Buttons >> 8) & 0xff);

                case 4: return RightJoyX;
                case 5: return RightJoyY;
                case 6: return LeftJoyX;
                case 7:
                    ACK = false;
                    SequenceNum = 0;
                    if (SwitchModeNext) {
                        ControllerMode = Mode.ConfigMode;
                        SwitchModeNext = false;
                    }
                    return LeftJoyY;

                default:
                    Console.WriteLine("Unkown sequence number for controller communication: " + SequenceNum);
                    ACK = false;
                    SequenceNum = 0;
                    return 0xFF;
            }
        }
        public byte ConfigModeResponse(uint data) {
            switch (SequenceNum++) {
                case 0:
                    CurrentCommand = data;  
                    return 0xF3; //0x41;
                case 1: return 0x5A;
                case 2:
                    switch (CurrentCommand) {
                        case 0x42: return  (byte)(Buttons & 0xff);
                        case 0x43: SwitchModeNext = data == 0x0; return 0x0;
                        case 0x44: LEDStatus_Temp = (byte)data; return 0x0;
                        case 0x45: return 0x1;
                        case 0x47: return 0x0;
                        case 0x46:
                            if (data == 0) {
                                VariableResponseA = new byte[] { 0x01, 0x02, 0x00, 0x0a };
                            }else if (data == 1) {
                                VariableResponseA = new byte[] { 0x01, 0x01, 0x01, 0x14 };
                            }
                            return 0x0;
                        case 0x4C: 
                            if(data == 0) {
                                VariableResponseB = 0x04;
                            }  else if (data == 1) {
                                VariableResponseB = 0x07;
                            }
                            return 0x0;

                        case 0x4D: byte temp = RumbleConfiguration[0]; RumbleConfiguration[0] = (byte)data; return temp;

                        default: throw new Exception("[PAD] Config Command: " + CurrentCommand.ToString("x"));
                    };

                case 3:
                    switch (CurrentCommand) {
                        case 0x42: return (byte)((Buttons >> 8) & 0xff);
                        case 0x43: return 0x0;
                        case 0x44: if (data == 0x02) { LEDStatus = LEDStatus_Temp; Console.WriteLine("[PAD] LED: " + LEDStatus); } return 0x0;
                        case 0x45: return 0x2;
                        case 0x46: return 0x0;
                        case 0x47: return 0x0;
                        case 0x4C: return 0x0;
                        case 0x4D: byte temp = RumbleConfiguration[1]; RumbleConfiguration[1] = (byte)data; return temp;

                        default: throw new Exception("[PAD] Config Command: " + CurrentCommand.ToString("x"));
                    };

                case 4:
                    switch (CurrentCommand) {
                        case 0x42: return RightJoyX;
                        case 0x43: return 0x0;
                        case 0x44: return 0x0;
                        case 0x45: return LEDStatus;    
                        case 0x46: return VariableResponseA[0];
                        case 0x47: return 0x2;
                        case 0x4C: return 0x0;
                        case 0x4D: byte temp = RumbleConfiguration[2]; RumbleConfiguration[2] = (byte)data; return temp;

                        default: throw new Exception("[PAD] Config Command: " + CurrentCommand.ToString("x"));
                    };

                case 5:
                    switch (CurrentCommand) {
                        case 0x42: return RightJoyY;
                        case 0x43: return 0x0;
                        case 0x44: return 0x0;
                        case 0x45: return 0x2;
                        case 0x46: return VariableResponseA[1];
                        case 0x47: return 0x0;
                        case 0x4C: return VariableResponseB;
                        case 0x4D: byte temp = RumbleConfiguration[3]; RumbleConfiguration[3] = (byte)data; return temp;

                        default: throw new Exception("[PAD] Config Command: " + CurrentCommand.ToString("x"));
                    };

                case 6:
                    switch (CurrentCommand) {
                        case 0x42: return LeftJoyX;
                        case 0x43: return 0x0;
                        case 0x44: return 0x0;
                        case 0x45: return 0x1;
                        case 0x46: return VariableResponseA[2];
                        case 0x47: return 0x1;
                        case 0x4C: return 0x0;
                        case 0x4D: byte temp = RumbleConfiguration[4]; RumbleConfiguration[4] = (byte)data; return temp;

                        default: throw new Exception("[PAD] Config Command: " + CurrentCommand.ToString("x"));
                    }; ;
            

                case 7:
                    ACK = false;
                    SequenceNum = 0;
                    if (SwitchModeNext) {
                        SwitchModeNext = false;
                        ControllerMode = Mode.NormalMode;
                    }

                    switch (CurrentCommand) {
                        case 0x42: return LeftJoyY;
                        case 0x43: return 0x0;
                        case 0x44: return 0x0;
                        case 0x45: return 0x0;
                        case 0x46: return VariableResponseA[3];
                        case 0x47: return 0x0;
                        case 0x4C: return 0x0;
                        case 0x4D: byte temp = RumbleConfiguration[5]; RumbleConfiguration[5] = (byte)data; return temp;

                        default: throw new Exception("[PAD] Config Command: " + CurrentCommand.ToString("x"));
                    };

                default:
                    Console.WriteLine("Unkown sequence number for controller communication: " + SequenceNum);
                    ACK = false;
                    SequenceNum = 0;
                    return 0xFF;
            }
        }

        //TODO: Optimize
        public void ReadInput(JoystickState externalController) {
            if (externalController == null) { 
                IsConnected = false;
                return;
            }
            else {
                IsConnected = true;
            }

            if (IgnoreInput) {
                Buttons = 0xFFFF;
                return;
            }


            for (int j = 0; j < externalController.ButtonCount; j++) {
                if (DualSenseDictionary.ContainsKey(j)) {
                    if (externalController.IsButtonDown(j)) {
                        int bit = ~(1 << DualSenseDictionary[j]);
                        Buttons &= (ushort)(bit);
                    }
                    else {
                        int bit = (1 << DualSenseDictionary[j]);
                        Buttons |= (ushort)(bit);
                    }
                }
            }

            if (externalController.IsButtonPressed(14)) { 
                IsAnalog = !IsAnalog;
                Console.WriteLine("[PAD] Analog Mode: " + (IsAnalog? "Enabled" : "Disabled"));
            }

            float leftX = externalController.GetAxis(0);
            float leftY = externalController.GetAxis(1);
            float rightX = externalController.GetAxis(2);
            float rightY = externalController.GetAxis(5);

            //Convert [-1 , 1] to [0 , 0xFF]
            RightJoyX = (byte)(((rightX + 1.0f) / 2.0f) * 0xFF);
            LeftJoyX = (byte)(((leftX + 1.0f) / 2.0f) * 0xFF);
            RightJoyY = (byte)(((rightY + 1.0f) / 2.0f) * 0xFF);
            LeftJoyY = (byte)(((leftY + 1.0f) / 2.0f) * 0xFF);
        }
    }
}
