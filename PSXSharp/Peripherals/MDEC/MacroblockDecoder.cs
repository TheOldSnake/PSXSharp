using System;
using System.Collections.Generic;
using System.Data;

namespace PSXSharp.Peripherals.MDEC {
    public class MacroblockDecoder {    //JPEG-style Macroblock Decoder

        public Range Range = new Range(0x1F801820, 5);

        //Status Register
        const uint InitialStatus = 0x80040000;

        uint DataOutFifoEmpty;   //31    Data-Out Fifo Empty (0=No, 1=Empty)
        uint DataInFifoFull;     //30    Data-In Fifo Full   (0=No, 1=Full, or Last word received)
        uint CommandBusy;        //29    Command Busy  (0=Ready, 1=Busy receiving or processing parameters)
        uint DataInRequest;      //28    Data-In Request  (set when DMA0 enabled and ready to receive data)
        uint DataOutRequest;     //27    Data-Out Request (set when DMA1 enabled and ready to send data)
        uint DataOutputDepth;    //26-25 Data Output Depth  (0=4bit, 1=8bit, 2=24bit, 3=15bit)      ;CMD.28-27
        uint DataOutputSigned;   //24    Data Output Signed (0=Unsigned, 1=Signed)                  ;CMD.26
        uint DataOutputBit15;    //23    Data Output Bit15  (0=Clear, 1=Set) (for 15bit depth only) ;CMD.25
                                 //22-19 Not used (seems to be always zero)
        uint CurrentBlock;       //18-16 Current Block (0..3=Y1..Y4, 4=Cr, 5=Cb) (or for mono: always 4=Y)     
        ushort WordsRemaining;   //15-0  Number of Parameter Words remaining minus 1  (FFFFh=None)  ;CMD.Bit0-15

        //Control
        uint DataInRequestEnabled;
        uint DataOutRequestEnabled;

        //Store required tables, could use arrays with so many pointers maybe
        private List<byte> LuminanceQuantTable = new List<byte>(64);        //iq_y
        private List<byte> ColorQuantTable = new List<byte>(64);            //iq_uv
        private List<short> ScaleTable = new List<short>(64);

        public Queue<ushort> Compresssed = new Queue<ushort>();
        public short[][] blk = {
            new short[64], new short[64], new short[64],
            new short[64], new short[64], new short[64]
        };

        short[] dst = new short[64];

        Macroblock CurrentMacroblock;
        Queue<Macroblock> FinalOutput = new Queue<Macroblock>();

        /*private int[] ZigZag = {
            0 ,1 ,5 ,6 ,14,15,27,28,
            2 ,4 ,7 ,13,16,26,29,42,
            3 ,8 ,12,17,25,30,41,43,
            9 ,11,18,24,31,40,44,53,
            10,19,23,32,39,45,52,54,
            20,22,33,38,46,51,55,60,
            21,34,37,47,50,56,59,61,
            35,36,48,49,57,58,62,63
        };*/

        //Inverted ZigZag => for i=0 to 63, zagzig[zigzag[i]]=i, next i
        private int[] ZagZig = {
             0,  1,  8, 16,  9,  2,  3, 10,
            17, 24, 32, 25, 18, 11,  4,  5,
            12, 19, 26, 33, 40, 48, 41, 34,
            27, 20, 13,  6,  7, 14, 21, 28,
            35, 42, 49, 56, 57, 50, 43, 36,
            29, 22, 15, 23, 30, 37, 44, 51,
            58, 59, 52, 45, 38, 31, 39, 46,
            53, 60, 61, 54, 47, 55, 62, 63
        };

        public enum MDECState {
            Idle,
            LoadingLuminanceQuantTable,
            LoadingLuminanceAndColorQuantTable,
            LoadingScaleTable,
            LoadingData
        }

        MDECState CurrentState = MDECState.Idle;

        public MacroblockDecoder() {
            WriteStatus(InitialStatus);   //Reset        
            DataInRequest = 1;
        }

        //TODO: Reading from DMA outputs the data in a different order than reading directly 
        public uint ReadWord(uint address) {
            uint offset = address - Range.Start;
            switch (offset) {
                case 0: return ReadCurrentMacroblock();              //Response/Data read
                case 4: return ReadStatus();                        //Status read
                default: throw new Exception("Unknown MDEC Read port: " + offset.ToString("x") + " - Full address: " + address.ToString("x"));
            }
        }
        public void WriteWord(uint address, uint value) {
            uint offset = address - Range.Start;
            switch (offset) {
                case 0: CommandAndParameters(value); break;
                case 4: WriteControl(value); break;
                default: throw new Exception("Unknown MDEC Write port: " + offset.ToString("x") + " - Full address: " + address.ToString("x"));
            }
        }
        private uint ReadStatus() {
            uint status = 0;
            status |= DataOutFifoEmpty << 31;
            status |= DataInFifoFull << 30;
            status |= CommandBusy << 29;
            status |= (DataInRequest & DataInRequestEnabled) << 28;
            status |= (DataOutRequest & DataOutRequestEnabled) << 27;
            status |= DataOutputDepth << 25;
            status |= DataOutputSigned << 24;
            status |= DataOutputBit15 << 23;
            status |= ((CurrentBlock + 4) % 6) << 16;
            status |= WordsRemaining;


            //Console.WriteLine("Reading: " + status.ToString("x"));
            //Console.WriteLine("N: " + FinalOutput.Count);

            return status;
        }
        private void WriteStatus(uint value) {
            DataOutFifoEmpty = (value >> 31) & 1;
            DataInFifoFull = (value >> 30) & 1;
            CommandBusy = (value >> 29) & 1;
            DataInRequest = (value >> 28) & 1;
            DataOutRequest = (value >> 27) & 1;
            DataOutputDepth = (value >> 25) & 0x3;
            DataOutputSigned = (value >> 24) & 1;
            DataOutputBit15 = (value >> 23) & 1;
            //CurrentBlock = (((value >> 16) & 0x7) + 4) % 6;     //Make 0,1 CB/CR
            //WordsRemaining = (ushort)(value & 0xF);
        }
        private void WriteControl(uint value) {
            /*  31    Reset MDEC (0=No change, 1=Abort any command, and set status=80040000h)
                30    Enable Data-In Request  (0=Disable, 1=Enable DMA0 and Status.bit28)
                29    Enable Data-Out Request (0=Disable, 1=Enable DMA1 and Status.bit27)
                28-0  Unknown/Not used - usually zero */
            bool reset = (value >> 31 & 1) == 1;
            DataInRequestEnabled = value >> 30 & 1;
            DataOutRequestEnabled = value >> 29 & 1;

            DataInRequest = DataInRequestEnabled;

            if (reset) {
                //Console.WriteLine("\n[MDEC] Reset!");
                WriteStatus(InitialStatus);   //Reset
                //LuminanceQuantTable.Clear();
                //ColorQuantTable.Clear();
                //ScaleTable.Clear();
                CurrentState = MDECState.Idle;
                FinalOutput.Clear();
                CurrentMacroblock = null;
                CurrentBlock = 0;
            }
        }
        public void CommandAndParameters(uint value) { //Find a better name for this thing

            switch (CurrentState) {
                case MDECState.LoadingScaleTable:
                    ScaleTable.AddRange(new short[] { (short)value, (short)(value >> 16) });
                    if (ScaleTable.Count == 64) {
                        CurrentState = MDECState.Idle;
                    }
                    return;

                case MDECState.LoadingLuminanceQuantTable:
                    LuminanceQuantTable.AddRange(new byte[] { (byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24) });
                    if (LuminanceQuantTable.Count == 64) {
                        CurrentState = MDECState.Idle;
                    }
                    return;
                case MDECState.LoadingLuminanceAndColorQuantTable:
                    if (LuminanceQuantTable.Count < 64) {
                        LuminanceQuantTable.AddRange(new byte[] { (byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24) });
                        return;
                    } else if (ColorQuantTable.Count < 64) {
                        ColorQuantTable.AddRange(new byte[] { (byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24) });
                        return;
                    } else {
                        CurrentState = MDECState.Idle;
                        break; 
                    }

                case MDECState.LoadingData:
                    Compresssed.Enqueue((ushort)value);
                    Compresssed.Enqueue((ushort)(value >> 16));
                    WordsRemaining--;
                    if (WordsRemaining == 0xFFFF) {
                        if (DataOutputDepth > 1) {
                            //Console.WriteLine("Decoding 15/24 bit");
                            CurrentState = MDECState.Idle;

                            while (Compresssed.Count > 0) {

                                if (CurrentMacroblock == null) {    //I don't like using null                    
                                    CurrentMacroblock = new Macroblock(DataOutputDepth);
                                }

                                while (CurrentBlock < 6) {
                                    if (rl_decode_block(ref blk[CurrentBlock], ref Compresssed, CurrentBlock < 2 ? ColorQuantTable : LuminanceQuantTable)) {
                                        idct_core(ref blk[CurrentBlock]);
                                    } else {
                                        //Console.WriteLine("15/24 bit return!");
                                        if (FinalOutput.Count > 0) {
                                            //WordsRemaining += (ushort)(CurrentMacroblock.Size() / 2);
                                            DataOutRequest = 1;
                                            DataOutFifoEmpty = 0;
                                            CommandBusy = 0;
                                            CurrentMacroblock = null;
                                        }
                                        return;         //Don't continue
                                    }
                                    CurrentBlock++;
                                }

                                CurrentBlock = 0;
                                k = 64;
                                val = 0;
                                n = 0;

                                yuv_to_rgb(ref blk[2], DataOutputSigned == 1, 0, 0);
                                yuv_to_rgb(ref blk[3], DataOutputSigned == 1, 8, 0);    //0,8 in PSX-SPX
                                yuv_to_rgb(ref blk[4], DataOutputSigned == 1, 0, 8);    //8,0 in PSX-SPX
                                yuv_to_rgb(ref blk[5], DataOutputSigned == 1, 8, 8);
                                FinalOutput.Enqueue(CurrentMacroblock);
                                //WordsRemaining += (ushort)(CurrentMacroblock.Size() / 2);
                                DataOutRequest = 1;
                                DataOutFifoEmpty = 0;
                                CommandBusy = 0;
                                CurrentMacroblock = null;
                            }
                            //Console.WriteLine("Done 15/24 bit");

                        } else {
                            //Console.WriteLine("Decoding 4/8 bit");
                            CurrentState = MDECState.Idle;

                            while (Compresssed.Count > 0) {
                                if (CurrentMacroblock == null) {    //I don't like using null                    
                                    CurrentMacroblock = new Macroblock(DataOutputDepth);
                                }
                                if (rl_decode_block(ref blk[0], ref Compresssed, LuminanceQuantTable)) {
                                    idct_core(ref blk[0]);
                                    y_to_mono(ref blk[0], DataOutputSigned == 1);
                                } else {
                                    //Console.WriteLine("4/8 bit return!");
                                    WordsRemaining += (ushort)(CurrentMacroblock.Size() / 2);
                                    DataOutRequest = 1;
                                    DataOutFifoEmpty = 0;
                                    CommandBusy = 0;
                                    CurrentMacroblock = null;
                                    return;
                                }
                                FinalOutput.Enqueue(CurrentMacroblock);
                                //WordsRemaining += (ushort)(CurrentMacroblock.Size() / 4);
                                DataOutFifoEmpty = 0;
                                DataOutRequest = 1;
                                CommandBusy = 0;
                                CurrentMacroblock = null;
                            }
                            //Console.WriteLine("Done 4/8 bit");
                        }
                        CurrentState = MDECState.Idle;
                    }
                    return;
            }

            //If we reach here then the value isn't a parameter of anything
            //Decode and Execute
            ExecuteCommand(value);
        }
        bool Incompleteblock = false;
       
        public uint ReadCurrentMacroblock() {

            if (FinalOutput.Count == 0) {
                //Console.WriteLine("[MDEC] out: 0xFFFFFFFF");
                return 0xFFFFFFFF;
            }

            uint word = FinalOutput.Peek().ReadNext();
            //WordsRemaining -= 4;
            if (FinalOutput.Peek().HasBeenRead) {
                FinalOutput.Dequeue();
                if (FinalOutput.Count == 0) {
                    DataOutRequest = 0;
                    DataOutFifoEmpty = 1;
                    CommandBusy = 0;
                    WordsRemaining = 0xFFFF;
                    //Console.WriteLine("[MDEC] Buffer finished");
                    //Console.WriteLine("[MDEC] Status: " + ReadStatus().ToString("x") + " -- Enum: " + CurrentState + " -- Hold: " + Incompleteblock);

                }
            }
            //Console.WriteLine("[MDEC] out: " + word.ToString("x"));

            return word;
        }

        private void y_to_mono(ref short[] yblk, bool IsSigned) {
            short y;
            int signedY0;
            int signedY1;
            switch (DataOutputDepth) {
                case 0:
                    for (int i = 0; i < 64; i += 2) {
                        y = (short)(yblk[i] & 0x1FF);
                        signedY0 = SignedXBits(y, 9);
                        signedY0 = Math.Clamp(signedY0, -128, 127);
                        if (!IsSigned) { signedY0 += 128; };

                        y = (short)(yblk[i + 1] & 0x1FF);
                        signedY1 = SignedXBits(y, 9);
                        signedY1 = Math.Clamp(signedY1, -128, 127);
                        if (!IsSigned) { signedY1 += 128; };

                        CurrentMacroblock.Write((byte)((uint)(signedY0 >> 4) | (uint)(signedY1 >> 4) << 4), i / 2);
                    }
                    break;

                case 1:
                    for (int i = 0; i < 64; i++) {
                        y = (short)(yblk[i] & 0x1FF);
                        signedY0 = SignedXBits(y, 9);
                        signedY0 = Math.Clamp(signedY0, -128, 127);
                        if (!IsSigned) { signedY0 += 128; };
                        CurrentMacroblock.Write((byte)signedY0, i);
                    }
                    break;
            }
        }

        private void yuv_to_rgb(ref short[] cblks, bool IsSigned, int xx, int yy) {
            short R, G, B, C_Y;   //Idk what to call Y, there is already small y

            for (int y = 0; y <= 7; y++) {
                for (int x = 0; x <= 7; x++) {
                    R = blk[0][(x + xx) / 2 + (y + yy) / 2 * 8];
                    B = blk[1][(x + xx) / 2 + (y + yy) / 2 * 8];
                    G = (short)(-0.3437f * B + -0.7143f * R);

                    R = (short)(1.402f * R);
                    B = (short)(1.772f * B);
                    C_Y = cblks[x + y * 8];

                    R = (short)Math.Clamp(C_Y + R, -128, 127);
                    G = (short)Math.Clamp(C_Y + G, -128, 127);
                    B = (short)Math.Clamp(C_Y + B, -128, 127);

                    if (!IsSigned) {
                        R += 128;
                        G += 128;
                        B += 128;
                    }

                    if (DataOutputDepth == 2) {                         //24 bpp
                        int index = (x + xx + (y + yy) * 16) * 3;
                        CurrentMacroblock.Write((byte)R, index);
                        CurrentMacroblock.Write((byte)G, index + 1);
                        CurrentMacroblock.Write((byte)B, index + 2);

                        // Console.WriteLine("[MDEC] 24 bit");

                    } else {                                          //15 bpp 
                        int index = (x + xx + (y + yy) * 16) * 2;
                        byte R5 = (byte)((R >> 3) & 0x1F);          //Convert to BGR555
                        byte G5 = (byte)((G >> 3) & 0x1F);
                        byte B5 = (byte)((B >> 3) & 0x1F);
                        byte bit15 = (byte)DataOutputBit15;

                        ushort color = (ushort)(R5 | G5 << 5 | B5 << 10 | bit15 << 15);

                        CurrentMacroblock.Write((byte)(color & 0xFF), index);
                        CurrentMacroblock.Write((byte)((color >> 8) & 0xFF), index + 1);
                    }
                }
            }
        }

        ushort k = 64;
        ushort n = 0;
        ushort q_scale = 0;
        int val = 0;

        //Returns true after the block is fully decoded
        private bool rl_decode_block(ref short[] blk, ref Queue<ushort> src, List<byte> qt) {

            if (k >= 63) {                                                    //New block
                k = 0;
                for (int i = 0; i < blk.Length; i++) {                        //initially zerofill all entries (for skip)
                    blk[i] = 0;
                }
                if(src.Count == 0) { return false; }
                n = src.Dequeue();

                while (n == 0xFE00) {                                        //ignore padding (FE00h as first halfword)
                    if (src.Count == 0) {
                        return false;
                    } else {
                        n = src.Dequeue();
                    }
                }

                q_scale = (ushort)((n >> 10) & 0x3F);                           //contains scale value(not "skip" value)
                val = SignedXBits(n & 0x3FF, 10) * qt[k];                       //calc first value(without q_scale / 8)(?)
            }


            for (;;) {
                if (q_scale == 0) { val = SignedXBits(n & 0x3FF, 10) * 2; }          //special mode without qt[k]
                val = Math.Clamp(val, -0x400, +0x3FF);                               //saturate to signed 11bit range
                //  val=val*scalezag[i]           ;<-- for "fast_idct_core" only

                if (q_scale > 0) { blk[ZagZig[k]] = (short)val; }                       //store entry(normal case)
                if (q_scale == 0) { blk[k] = (short)val; };                            //store entry(special, no zigzag)                 

                if (src.Count == 0) { /*Console.WriteLine("[MDEC] Src reached 0")*/; return false; }

                n = src.Dequeue();                                                   //;get next entry (or FE00h end code)
                k = (ushort)(k + ((n >> 10) & 0x3F) + 1);                            //skip zerofilled entries
                if (k >= 63) {
                    return true;
                }
                val = (SignedXBits(n & 0x3FF, 10) * qt[k] * q_scale + 4) / 8;       //calc value for next entry
            }

            return true;
        }

        private void idct_core(ref short[] src) {

            long sum;
            for (int pass = 0; pass <= 1; pass++) {
                for (int x = 0; x <= 7; x++) {
                    for (int y = 0; y <= 7; y++) {
                        sum = 0;
                        for (int z = 0; z <= 7; z++) {
                            sum = sum + src[y + z * 8] * (ScaleTable[x + z * 8] / 8);
                        }
                        dst[x + y * 8] = (short)((sum + 0x0fff) / 0x2000);
                    }
                }
                (src, dst) = (dst, src);
            }
        }

        private int SignedXBits(int val, int bit) {
            int shift = 32 - bit;
            return val << shift >> shift;
        }

        private void ExecuteCommand(uint value) {
            uint opcode = value >> 29;
            switch (opcode) {
                case 0x01: DecodeMacroblock(value); break;
                case 0x02: SetQuantTable(value); break;
                case 0x03: SetScaleTable(value); break;

                case 0x00:
                case 0x04:
                case 0x05:
                case 0x06:
                case 0x07:
                    Nop(value); break;

                default: throw new Exception("Unknown MDEC Command: 0x" + opcode.ToString("x"));
            }
        }
        private void Nop(uint value) {
            /* This command has no function. Command bits 25-28 are reflected to Status bits 23-26 as usually. 
            Command bits 0-15 are reflected to Status bits 0-15 (similar as the "number of parameter words" for MDEC(1),
            but without the "minus 1" effect, and without actually expecting any parameters). */
            //Console.WriteLine("\n[MDEC] NOP");
            DataOutputBit15 = (value >> 25) & 1;
            DataOutputSigned = (value >> 26) & 1;
            DataOutputDepth = (value >> 27) & 0x3;
            //WordsRemaining = (ushort)(value & 0xFFFF);
        }
        private void SetQuantTable(uint command) {
            LuminanceQuantTable.Clear();
            bool loadingBoth = (command & 1) == 1;
            if (loadingBoth) {
                //Console.WriteLine("\n[MDEC] Loading Both Tables");
                CurrentState = MDECState.LoadingLuminanceAndColorQuantTable;
                ColorQuantTable.Clear();

            } else {
                //Console.WriteLine("\n[MDEC] Loading Luminance Quant Table ");
                CurrentState = MDECState.LoadingLuminanceQuantTable;
            }
            //Bit25-28 are copied to STAT.23-26
            DataOutputBit15 = command >> 23 & 1;
            DataOutputSigned = command >> 24 & 1;
            DataOutputDepth = command >> 25 & 0x3;
            //WordsRemaining = (ushort)((64 << ((int)command & 1)) - 1);
        }
        private void SetScaleTable(uint command) {
            ScaleTable.Clear();
            //Bit25-28 are copied to STAT.23-26
            DataOutputBit15 = command >> 23 & 1;
            DataOutputSigned = command >> 24 & 1;
            DataOutputDepth = command >> 25 & 0x3;
            //WordsRemaining = 64 - 1;
            CurrentState = MDECState.LoadingScaleTable;
            //Console.WriteLine("\n[MDEC] Loading Scale Table ");

        }
        private void DecodeMacroblock(uint value) {
            uint depth = (value >> 27) & 0x3;
            uint signed = (value >> 26) & 1;
            uint outBit15 = (value >> 25) & 1;
            ushort numberOfParameters = (ushort)(value & 0xFFFF);

            DataOutputDepth = depth;
            DataOutputSigned = signed;
            DataOutputBit15 = outBit15;
            WordsRemaining = (ushort)(numberOfParameters - 1);

            CurrentState = MDECState.LoadingData;

            CommandBusy = 1;

            /*Console.WriteLine("\nDecode Macroblock -> " + value.ToString("x"));
            Console.WriteLine("Data Output Depth: " + depth);
            Console.WriteLine("Data Output Sign: " + signed);
            Console.WriteLine("Data Output Bit15: " + outBit15);
            Console.WriteLine("Number of parameters: " + numberOfParameters);*/

        }
    }
}
