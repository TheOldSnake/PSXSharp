using OpenTK.Windowing.GraphicsLibraryFramework;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.Window;

namespace PSXSharp.Peripherals.IO {
    public class Controller {
        public bool ACK;
        public bool IsConnected;
        public bool IgnoreInput;
        public ushort ButtonsRegister = 0xFFFF;     //0 = Pressed, 1 = Released
        public byte RightJoyX = 0x80;  
        public byte RightJoyY = 0x80;
        public byte LeftJoyX = 0x80;
        public byte LeftJoyY = 0x80;
        public byte LEDStatus = 0x1;
        public byte LEDStatus_Temp = 0x0;
        public bool IsAnalog = false;

        bool SwitchModeNext = false;

        public int SequenceNum;
        public enum Mode { NormalMode, ConfigMode  }
        Mode ControllerMode = Mode.NormalMode;
        public uint CurrentCommand; //Mainly for Config
        byte[] VariableResponseA;
        byte VariableResponseB;
        byte[] RumbleConfiguration = new byte[] { 0x00, 0x01, 0xFF, 0xFF, 0xFF, 0xFF };    //Most games will use 00h 01h FFh FFh FFh FFh

        public enum Buttons {
            Select = 0,
            L3 = 1,
            R3 = 2,
            Start = 3,
            PadUp = 4,
            PadRight = 5,
            PadDown = 6,
            PadLeft = 7,
            L2 = 8,
            R2 = 9,
            L1 = 10,
            R1 = 11,
            Triangle = 12,
            Circle = 13,
            Cross = 14, 
            Square = 15
        }


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

        //Left X, Left Y, Right X, Right Y
        public static readonly int[] DualSenseAxis = [0, 1, 2, 5]; 

        public static readonly Dictionary<int, int> XboxDictionary = new Dictionary<int, int>() {
               {0, 14},      //X
               {1, 13},      //Circle
               {2, 15},      //Square
               {3, 12},      //Triangle
               {4, 10},      //L1
               {5, 11},      //R1
               {6, 0},       //Select
               {7, 3},       //Start
               {8, 1},       //L3
               {9, 2},       //R3
               {10, 4},      //Pad up
               {11, 5},      //Pad right
               {12, 6},      //Pad down
               {13, 7},      //Pad Left

               //L2 is Axis 4
               //R2 is Axis 5 
            };

        //Left X, Left Y, Right X, Right Y
        public static readonly int[] XboxAxis = [0, 1, 2, 3]; 

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

                    return (byte)(ButtonsRegister & 0xff);
                case 3:
                    if (!IsAnalog) {
                        SequenceNum = 0;
                        ACK = false;
                    }
                    return (byte)(ButtonsRegister >> 8 & 0xff);

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
                        case 0x42: return  (byte)(ButtonsRegister & 0xff);
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
                        case 0x42: return (byte)(ButtonsRegister >> 8 & 0xff);
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

        public void ReadInput(JoystickState controller) {
            if (controller == null) { 
                IsConnected = false;
                return;
            } else {
                IsConnected = true;
            }

            if (IgnoreInput) {
                ButtonsRegister = 0xFFFF;
                return;
            }

            Dictionary<int, int> dictionary;
            int[] axis;

            switch (controller.Name) {
                //DualSense gets detected as "Wireless Controller" 
                case "Wireless Controller": 
                    dictionary = DualSenseDictionary;
                    axis = DualSenseAxis;
                    break;

                //(All?) Xinput controllers are detected as "Xbox Controller"
                case "Xbox Controller":
                    dictionary = XboxDictionary;
                    axis = XboxAxis;
                    break;

                //If unknown controller then return
                default: Console.WriteLine($"Unknown Controller Name: {controller.Name}"); return;    
            }

            UpdateButtonsStates(controller, dictionary);
            UpdateAnalogState(controller);
            UpdateAxesStates(controller, axis);
        }

        private void UpdateButtonsStates(JoystickState controller, Dictionary<int, int> dictionary) {
            //Loop over all buttons and clear the bits if pressed
            int bitMask;

            for (int j = 0; j < controller.ButtonCount; j++) {
                if (dictionary.ContainsKey(j)) {
                    if (controller.IsButtonDown(j)) {
                        bitMask = ~(1 << dictionary[j]);
                        ButtonsRegister &= (ushort)bitMask;
                    } else {
                        bitMask = 1 << dictionary[j];
                        ButtonsRegister |= (ushort)bitMask;
                    }
                }
            }

            if (controller.Name == "Xbox Controller") {
                //Handle L2 and R2 because they were not in the buttons array
                if (controller.GetAxis(4) > -1) {
                    bitMask = ~(1 << 8);
                    ButtonsRegister &= (ushort)bitMask;
                } else {
                    bitMask = 1 << 8;
                    ButtonsRegister |= (ushort)bitMask;
                }

                if (controller.GetAxis(5) > -1) {
                    bitMask = ~(1 << 9);
                    ButtonsRegister &= (ushort)bitMask;
                } else {
                    bitMask = 1 << 9;
                    ButtonsRegister |= (ushort)bitMask;
                }
            }
        }

        private void UpdateAnalogState(JoystickState controller) {
            bool analog;
            if (controller.Name == "Xbox Controller") {
                //Turn analog on if both L3 & R3 are pressed
                analog = controller.IsButtonPressed(8) && controller.IsButtonPressed(9);
            } else {
                //Turn analog on if mute is pressed
                analog = controller.IsButtonPressed(14);
            }

            if (analog) {
                IsAnalog = !IsAnalog;
                Console.WriteLine($"[PAD] Analog Mode: {(IsAnalog ? "Enabled" : "Disabled")}");
            }
        }

        private void UpdateAxesStates(JoystickState controller, int[] axis) {
            float leftX = controller.GetAxis(axis[0]);
            float leftY = controller.GetAxis(axis[1]);
            float rightX = controller.GetAxis(axis[2]);
            float rightY = controller.GetAxis(axis[3]);

            RightJoyX = NormalizeAxis(rightX);
            LeftJoyX = NormalizeAxis(leftX);
            RightJoyY = NormalizeAxis(rightY);
            LeftJoyY = NormalizeAxis(leftY);
        }

        private byte NormalizeAxis(float value) {
            // Convert [-1, 1] to [0, 255]
            return (byte)((value + 1f) * 0.5f * 0xFF);
        }
    }
}
