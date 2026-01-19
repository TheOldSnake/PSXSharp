using NAudio.Wave;
using PSXSharp.Core;
using PSXSharp.Peripherals.SPU;
using System;
using System.Runtime.InteropServices;
using static PSXSharp.CDROMDataController;
using static PSXSharp.Peripherals.SPU.Voice.ADSR;

namespace PSXSharp {
    public class SPU {                            //Thanks to BlueStorm, lots of things are here "inspired" :D
        public const uint BASE_ADDRESS = 0x1F801C00;
        public const int CYCLES_PER_SAMPLE = 0x300;
        public Range range = new Range(BASE_ADDRESS, 640);

        byte[] RAM = new byte[512*1024];

        UInt16 SPUCNT;
        bool reverbEnabled;
        bool SPUEnable;
        bool IRQ9Enable;
        bool CDAudioEnable;
        bool CDReverbEnable;
        bool SPUMuted;

        //STAT:
        byte SPU_Mode;                          //0-5
        byte IRQ_Flag;                          //6
        public byte DMA_Read_Write_Request;     //7 seems to be same as SPUCNT.Bit5
        public byte DMA_Write_Request;          //8            
        public byte DMA_Read_Request;           //9            
        byte Data_transfer_busy;                //10 (1 = busy)           
        byte Writing_Capture_Buffers;           //11 Writing to First/Second half of Capture Buffers (0=First, 1=Second)

        Int16 mainVolumeLeft;
        Int16 mainVolumeRight;
        Int16 vLOUT;
        Int16 vROUT;

        uint KOFF;
        uint KON;

        uint PMON;

        uint NON;           //Noise mode enable
        uint EON;           //Echo 

        uint CDInputVolume;
        uint external_Audio_Input_Volume;
        CDROMDataController CDDataControl;

        UInt16 transfer_Control;
        uint transfer_address;
        uint currentAddress;
        uint reverbCurrentAddress;
        readonly Voice[] voices = new Voice[24];
        uint SPU_IRQ_Address;

        private int clk_counter = 0;
        public const uint CYCLES_PER_SAMPLE = 0x300;
        byte[] outputBuffer = new byte[2048];
        int outputBufferPtr = 0;
        int sumLeft;
        int sumRight;
        private int reverbCounter = 0;
        uint captureOffset = 0;

        //Reverb registers
        ushort mBASE;   //(divided by 8)
        ushort dAPF1;   //Type: disp
        ushort dAPF2;   //Type: disp
        short vIIR;     //Type: volume
        short vCOMB1;   //Type: volume
        short vCOMB2;   //Type: volume
        short vCOMB3;   //Type: volume
        short vCOMB4;   //Type: volume
        short vWALL;    //Type: volume
        short vAPF1;    //Type: volume
        short vAPF2;    //Type: volume
        ushort mLSAME;  //Type: src/dst
        ushort mRSAME;  //Type: src/dst
        ushort mLCOMB1; //Type: src
        ushort mRCOMB1; //Type: src
        ushort mLCOMB2; //Type: src
        ushort mRCOMB2; //Type: src
        ushort dLSAME;  //Type: src
        ushort dRSAME;  //Type: src
        ushort mLDIFF;  //Type: src/dst
        ushort mRDIFF;  //Type: src/dst
        ushort mLCOMB3; //Type: src
        ushort mRCOMB3; //Type: src
        ushort mLCOMB4; //Type: src
        ushort mRCOMB4; //Type: src
        ushort dLDIFF;  //Type: src
        ushort dRDIFF;  //Type: src
        ushort mLAPF1;  //Type: src/dst
        ushort mRAPF1;  //Type: src/dst
        ushort mLAPF2;  //Type: src/dst
        ushort mRAPF2;  //Type: src/dst
        short vLIN;     //Type: volume
        short vRIN;     //Type: volume

        private WaveOutEvent waveOutEvent = new WaveOutEvent();
        private BufferedWaveProvider bufferedWaveProvider = new BufferedWaveProvider(new WaveFormat());
        //private WaveFileWriter waveFileWriter = new WaveFileWriter("output.wav", new WaveFormat());     //For audio dumping

        /* Default wave format: 44,1kHz, 16 bit, stereo */

        public Action SPUCallback;
        public SPU(ref CDROMDataController CDControl) {
           for (int i = 0; i < voices.Length; i++) { 
                voices[i] = new Voice();
           }

            bufferedWaveProvider.DiscardOnBufferOverflow = true;
            bufferedWaveProvider.BufferDuration = new TimeSpan(0, 0, 0, 0, 300);
            waveOutEvent.Init(bufferedWaveProvider);
            this.CDDataControl = CDControl;
            SPUCallback = SPUEvent;
        }

        public void StoreHalf(uint address, UInt16 value) {
            uint offset = address - range.start;
            switch (offset) {

                case uint when ((offset + BASE_ADDRESS) >= 0x1F801C00 && (offset + BASE_ADDRESS) <= 0x1F801D7F):        //Voice 0...23 

                    uint index = (((offset + BASE_ADDRESS) & 0xFF0) >> 4) - 0xC0;         //index = offset/16 - 0xC0    (Inverse of the equation in psx-spx)
                    
                    switch ((offset + BASE_ADDRESS) & 0xf) {

                        case 0x0: voices[index].volumeLeft = (short)value; break;
                        case 0x2: voices[index].volumeRight = (short)value; break;
                        case 0x4: voices[index].ADPCM_Pitch = value; break;
                        case 0x6: voices[index].ADPCM = value; break;
                        case 0x8: voices[index].setADSR_LOW(value); break;
                        case 0xA: voices[index].setADSR_HI(value); break;
                        case 0xC: voices[index].adsr.adsrVolume = value; break;
                        case 0xE: voices[index].ADPCM_RepeatAdress = value; break;
                        default: throw new Exception("Unknown voice register: " + (offset & 0xf).ToString("x"));
                    
                    }
             
                    break;

                case 0x1aa: setCtrl(value); break;
                case 0x180: mainVolumeLeft = (short)value; break;
                case 0x182: mainVolumeRight = (short)value; break;
                case 0x184: vLOUT = (short)value; break;
                case 0x186: vROUT = (short)value; break;
                case 0x188: KON = (KON & 0xFFFF0000) | value; break;
                case 0x18a: KON = (KON & 0x0000FFFF) | ((uint)(value << 16)); break;
                case 0x18c: KOFF = (KOFF & 0xFFFF0000) | value; break;
                case 0x18e: KOFF = (KOFF & 0x0000FFFF) | ((uint)(value << 16)); break;
                case 0x190: PMON = (PMON & 0xFFFF0000) | value; break;
                case 0x192: PMON = (PMON & 0x0000FFFF) | ((uint)(value << 16)); break;
                case 0x194: NON = (NON & 0xFFFF0000) | value; break;
                case 0x196: NON = (NON & 0x0000FFFF) | ((uint)(value << 16)); break;
                case 0x198: EON = (EON & 0xFFFF0000) | value; break;
                case 0x19a: EON = (EON & 0x0000FFFF) | ((uint)(value << 16)); break;
                case 0x1b0: CDInputVolume = (CDInputVolume & 0xFFFF0000) | value; break;
                case 0x1b2: CDInputVolume = (CDInputVolume & 0x0000FFFF) | ((uint)(value << 16)); break;
                case 0x1b4: external_Audio_Input_Volume = (external_Audio_Input_Volume & 0xFFFF0000) | value; break;
                case 0x1b6: external_Audio_Input_Volume = (external_Audio_Input_Volume & 0x0000FFFF) | ((uint)(value << 16)); break;
                case 0x1ac: transfer_Control = value; break;
                case 0x1a4: SPU_IRQ_Address = ((uint)value) << 3; break;    
                case 0x1a6:
                    transfer_address = value;            //Store adress devided by 8
                    currentAddress = ((uint)value) << 3; //Store adress multiplied by 8, this one increments while the other does not
                    break;

                case 0x1a8:
                    if((transfer_Control >> 1 & 7) != 2) { throw new Exception(); }
                    currentAddress &= 0x7FFFF;
                    if ((currentAddress <= SPU_IRQ_Address) && ((currentAddress + 1) >= SPU_IRQ_Address)) { SPU_IRQ(); }
                    RAM[currentAddress++] = (byte)(value & 0xFF);
                    RAM[currentAddress++] = (byte)((value >> 8) & 0xFF);

                    break;

                case 0x19C:     //Read only?
                    for (int i = 0; i < 16; i++) {
                        voices[i].ENDX = (uint)((value >> i) & 1);
                    }
                    break;

                case 0x19E:
                    for (int i = 16; i < voices.Length; i++) {
                        voices[i].ENDX = (uint)((value >> (i - 16)) & 1);
                    }
                    break;

                //Reverb area
                case 0x1a2: 
                    mBASE = value;
                    reverbCurrentAddress = (uint)value << 3;
                    break;

                case 0x1c0: dAPF1 = value; break;
                case 0x1c2: dAPF2 = value; break;
                case 0x1c4: vIIR = (short)value; break;
                case 0x1c6: vCOMB1 = (short)value; break;
                case 0x1c8: vCOMB2 = (short)value; break;
                case 0x1cA: vCOMB3 = (short)value; break;
                case 0x1cC: vCOMB4 = (short)value; break;
                case 0x1cE: vWALL = (short)value; break;
                case 0x1D0: vAPF1 = (short)value; break;
                case 0x1D2: vAPF2 = (short)value; break;
                case 0x1D4: mLSAME = value; break;
                case 0x1D6: mRSAME = value; break;
                case 0x1D8: mLCOMB1 = value; break;
                case 0x1DA: mRCOMB1 = value; break;
                case 0x1DC: mLCOMB2 = value; break;
                case 0x1DE: mRCOMB2 = value; break;
                case 0x1E0: dLSAME = value; break;
                case 0x1E2: dRSAME = value; break;
                case 0x1E4: mLDIFF = value; break;
                case 0x1E6: mRDIFF = value; break;
                case 0x1E8: mLCOMB3 = value; break;
                case 0x1EA: mRCOMB3 = value; break;
                case 0x1EC: mLCOMB4 = value; break;
                case 0x1EE: mRCOMB4 = value; break;
                case 0x1F0: dLDIFF = value; break;
                case 0x1F2: dRDIFF = value; break;
                case 0x1F4: mLAPF1 = value; break;
                case 0x1F6: mRAPF1 = value; break;
                case 0x1F8: mLAPF2 = value; break;
                case 0x1FA: mRAPF2 = value; break;
                case 0x1FC: vLIN = (short)value; break;
                case 0x1FE: vRIN = (short)value; break;

                default: throw new NotImplementedException("Offset: " +offset.ToString("x")+ "\n" 
                                                      +"Full address: 0x" + (offset+0x1f801c00).ToString("x"));
            }
        }
        public UInt16 LoadHalf(uint address) {
            uint offset = address - range.start;
            ushort endx = 0;

            switch (offset) {
                case 0x1aa: return SPUCNT;
                case 0x1ae: return readStat();
                case 0x180: return (ushort)mainVolumeLeft;
                case 0x182: return (ushort)mainVolumeRight;
                case 0x184: return (ushort)vLOUT;
                case 0x186: return (ushort)vROUT;
                case 0x188: return (ushort)KON;
                case 0x18a: return (ushort)(KON>>16);
                case 0x18c: return (ushort)KOFF;
                case 0x18e: return (ushort)(KOFF>>16);
                case 0x1ac: return transfer_Control;

                case uint when ((offset + BASE_ADDRESS) >= 0x1F801C00 && (offset + BASE_ADDRESS) <= 0x1F801D7F):        //Voice 0...23 
                    uint index = (((offset + BASE_ADDRESS) & 0xFF0) >> 4) - 0xC0;         //index = offset/16 - 0xC0    (Inverse of the equation in psx-spx)

                    switch ((offset + BASE_ADDRESS) & 0xf) {

                        case 0x0: return (ushort)voices[index].volumeLeft;
                        case 0x2: return (ushort)voices[index].volumeRight;
                        case 0x4: return voices[index].ADPCM_Pitch;
                        case 0x6: return voices[index].ADPCM;
                        case 0x8: return voices[index].adsr.adsrLOW;
                        case 0xA: return voices[index].adsr.adsrHI;
                        case 0xC: return voices[index].adsr.adsrVolume;
                        case 0xE: return voices[index].ADPCM_RepeatAdress;

                        default: throw new Exception("Unknown voice register: " + (offset & 0xf).ToString("x"));

                    }

                // 1F801E00h..1F801E5Fh - Voice 0..23 Internal Registers

                case uint when ((offset + BASE_ADDRESS) >= 0x1F801E00 && (offset + BASE_ADDRESS) <= 0x1F801E5F):
                    return 0;

                //1F801D98h - Voice 0..23 Reverb mode aka Echo On (EON) (R/W)
                case 0x190: return (ushort)PMON;
                case 0x192: return (ushort)(PMON >> 16);
                case 0x194: return (ushort)NON;
                case 0x196: return (ushort)(NON >> 16);
                case 0x198: return (ushort)EON;
                case 0x19a: return (ushort)(EON >> 16);
                case 0x19C:     //Read only?
                    for (int i = 0; i < 16; i++) {
                        endx |= (ushort)(voices[i].ENDX << i);
                    }
                    return endx;
                case 0x19E:
                    for (int i = 16; i < voices.Length; i++) {
                        endx |= (ushort)(voices[i].ENDX << i);
                    }
                    return endx;

                case 0x1b0: return (ushort)CDInputVolume;
                case 0x1b2: return (ushort)(CDInputVolume >> 16);
                case 0x1b8: return (ushort)mainVolumeLeft;  
                case 0x1ba: return (ushort)mainVolumeRight;
                case 0x1a4: return (ushort)(SPU_IRQ_Address >> 3);
                case 0x1a6: return (ushort)transfer_address;
                case 0x1b4: return (ushort)external_Audio_Input_Volume;
                case 0x1b6: return (ushort)(external_Audio_Input_Volume >> 16);

                //Reverb area
                case 0x1a2: return mBASE;               
                case 0x1c0: return dAPF1; 
                case 0x1c2: return dAPF2;
                case 0x1c4: return (ushort)vIIR;
                case 0x1c6: return (ushort)vCOMB1;
                case 0x1c8: return (ushort)vCOMB2;
                case 0x1cA: return (ushort)vCOMB3;
                case 0x1cC: return (ushort)vCOMB4;
                case 0x1cE: return (ushort)vWALL;
                case 0x1D0: return (ushort)vAPF1;
                case 0x1D2: return (ushort)vAPF2;
                case 0x1D4: return mLSAME;
                case 0x1D6: return mRSAME;
                case 0x1D8: return mLCOMB1;
                case 0x1DA: return mRCOMB1;
                case 0x1DC: return mLCOMB2;
                case 0x1DE: return mRCOMB2;
                case 0x1E0: return dLSAME;
                case 0x1E2: return dRSAME;
                case 0x1E4: return mLDIFF;
                case 0x1E6: return mRDIFF;
                case 0x1E8: return mLCOMB3;
                case 0x1EA: return mRCOMB3;
                case 0x1EC: return mLCOMB4;
                case 0x1EE: return mRCOMB4;
                case 0x1F0: return dLDIFF;
                case 0x1F2: return dRDIFF;
                case 0x1F4: return mLAPF1;
                case 0x1F6: return mRAPF1;
                case 0x1F8: return mLAPF2;
                case 0x1FA: return mRAPF2;
                case 0x1FC: return (ushort)vLIN;
                case 0x1FE: return (ushort)vRIN;

                default: throw new NotImplementedException("Offset: " + offset.ToString("x") + "\n"
                                                      + "Full address: 0x" + (offset + 0x1f801c00).ToString("x"));
            }
        }

        public uint LoadWord(uint address) {
            return LoadHalf(address) | (((uint)LoadHalf(address + 2)) << 16);   //Why repeat the whole thing when you can do this lol
        }

        private ushort readStat() {
            uint status = 0;

            status |= SPU_Mode;
            status |= ((uint)IRQ_Flag) << 6;
            status |= ((uint)DMA_Read_Write_Request) << 7;
            status |= ((uint)DMA_Write_Request) << 8;
            status |= ((uint)DMA_Read_Request) << 9;
            status |= ((uint)Data_transfer_busy) << 10;
            status |= ((uint)Writing_Capture_Buffers) << 11;

            //12-15 are uknown/unused (seems to be usually zero)

            return (ushort)status;
        }

        public void setCtrl(UInt16 value) {
            SPUCNT = value;
            CDAudioEnable = (value & 1) == 1;
            CDReverbEnable = ((value >> 2) & 1) == 1;
            DMA_Read_Write_Request = (byte)((value >> 5) & 0x1);
            SPU_Mode = (byte)(value & 0x3F);
            IRQ9Enable = ((SPUCNT >> 6) & 1) == 1;
            reverbEnabled = ((value >> 7) & 1) == 1;     //Only affects Reverb bufffer write, SPU can still read from reverb area

            //8-9   Noise Frequency Step    (0..03h = Step "4,5,6,7")
            //10-13 Noise Frequency Shift   (0..0Fh = Low .. High Frequency)

            SPUMuted = ((value >> 14) & 1) == 0;
            SPUEnable = ((SPUCNT >> 15) & 1) == 1;

            if (!SPUEnable) {
                for (int i = 0; i < voices.Length; i++) {
                    voices[i].adsr.setPhase(Phase.Off);
                }
            }

            if (!IRQ9Enable) {
                IRQ_Flag = 0;
            }
            uint dmaMode = ((uint)((value >> 4) & 0x3));
            switch (dmaMode) {
                case 0: //Stop
                case 1: //Manual
                    DMA_Read_Request = 0;
                    DMA_Write_Request = 0;
                    break;

                case 2: DMA_Write_Request = 1; break;
                case 3: DMA_Read_Request = 1;  break;
            }

        }

        public void SPUEvent() {        
            reverbCounter = (reverbCounter + 1) & 1;    //For half the frequency

            uint edgeKeyOn = KON;
            uint edgeKeyOff = KOFF;
            KON = 0;
            KOFF = 0;

            sumLeft = 0;
            sumRight = 0;
            int reverbLeft = 0;
            int reverbRight = 0;
            int reverbLeft_Input = 0;
            int reverbRight_Input = 0;
            bool voiceHitAddress = false;

            for (int i = 0; i < voices.Length; i++) {

                if ((edgeKeyOn & (1 << i)) != 0) {
                    voices[i].keyOn();
                }

                if ((edgeKeyOff & (1 << i)) != 0) {
                    voices[i].keyOff();
                }

                if (voices[i].adsr.phase == Voice.ADSR.Phase.Off) {
                    //voices[i].lastSample = 0;
                    continue;
                }

                short sample = 0;

                if ((NON & (1 << i)) == 0) {

                    if (!voices[i].isLoaded) {
                        voices[i].loadSamples(ref RAM, SPU_IRQ_Address);
                        voices[i].decodeADPCM();
                        voiceHitAddress = voiceHitAddress || voices[i].hit_IRQ_Address;
                        voices[i].hit_IRQ_Address = false;
                    }

                    sample = voices[i].interpolate();
                    modulatePitch(i);
                    voices[i].checkSamplesIndex();
                } else {
                    Console.WriteLine("[SPU] Noise generator !"); //Haven't seen something use it yet
                    voices[i].adsr.adsrVolume = 0;
                    voices[i].lastSample = 0;
                }

                sample = (short)((sample * voices[i].adsr.adsrVolume) >> 15);
                voices[i].adsr.ADSREnvelope();
                voices[i].lastSample = sample;

                sumLeft += (sample * voices[i].getVolumeLeft()) >> 15;
                sumRight += (sample * voices[i].getVolumeRight()) >> 15;

                if (((EON >> i) & 1) == 1) {   //Adding samples from any channel with active reverb
                    reverbLeft_Input += (sample * voices[i].getVolumeLeft()) >> 15;
                    reverbRight_Input += (sample * voices[i].getVolumeRight()) >> 15;
                }

            }

            //Merge in CD-Audio (CD-DA and uncompressed XA-ADPCM), read one L/R sample each tick (tick rate is 44.1khz so it should match the sample rate)
            //CD Samples are consumed even if CD audio is disabled, they will also end up in the capture buffer 
            int cdSamples = CDDataControl.CDAudioSamples.Count;
            short CDAudioLeft = 0;
            short CDAudioRight = 0;
            short CDAudioLeft_BeforeVol = 0;
            short CDAudioRight_BeforeVol = 0;
            if (cdSamples > 0) {
                short CDLeftVolume = (short)CDInputVolume;
                short CDRightVolume = (short)(CDInputVolume >> 16);
                CDAudioLeft_BeforeVol = CDDataControl.CDAudioSamples.Dequeue();
                CDAudioRight_BeforeVol = CDDataControl.CDAudioSamples.Dequeue();
                CDAudioLeft += (short)((CDAudioLeft_BeforeVol * CDLeftVolume) >> 15);
                CDAudioRight += (short)((CDAudioRight_BeforeVol * CDRightVolume) >> 15);
            }
            captureBuffers(0x000 + captureOffset, CDAudioLeft_BeforeVol);               //Capture CD Audio left (before *volume)
            captureBuffers(0x400 + captureOffset, CDAudioRight_BeforeVol);              //Capture CD Audio right (before *volume)

            sumLeft += CDAudioEnable ? CDAudioLeft : 0;
            sumRight += CDAudioEnable ? CDAudioRight : 0;
            reverbLeft_Input += (CDAudioEnable && CDReverbEnable) ? CDAudioLeft : 0;
            reverbRight_Input += (CDAudioEnable && CDReverbEnable) ? CDAudioRight : 0;

            captureBuffers(0x800 + captureOffset, voices[1].lastSample);             //Capture Voice 1
            captureBuffers(0xC00 + captureOffset, voices[3].lastSample);             //Capture Voice 3
            captureOffset += 2;
            captureOffset &= 0x3FF;

            if (reverbCounter == 1) {
                (reverbLeft, reverbRight) = processReverb(reverbLeft_Input, reverbRight_Input);
            }

            sumLeft += reverbLeft;
            sumRight += reverbRight;

            sumLeft = (Math.Clamp(sumLeft, -0x8000, 0x7FFE) * mainVolumeLeft) >> 15;
            sumRight = (Math.Clamp(sumRight, -0x8000, 0x7FFE) * mainVolumeRight) >> 15;

            //SPU Mute
            sumLeft = SPUMuted ? 0 : sumLeft;
            sumRight = SPUMuted ? 0 : sumRight;

            outputBuffer[outputBufferPtr++] = (byte)sumLeft;
            outputBuffer[outputBufferPtr++] = (byte)(sumLeft >> 8);
            outputBuffer[outputBufferPtr++] = (byte)sumRight;
            outputBuffer[outputBufferPtr++] = (byte)(sumRight >> 8);

            if (outputBufferPtr >= 2048) {
                playAudio(outputBuffer);
                outputBufferPtr -= 2048;
            }

            if (voiceHitAddress) {
                SPU_IRQ();
            }

            //Schedule Next Event
            Scheduler.ScheduleEvent(0x300, SPUCallback, Event.SPU);
        }

        private void captureBuffers(uint address, short value) { //Experimental 
            Span<byte> Memory = new Span<byte>(RAM, (int)address, 2);
            MemoryMarshal.Write<short>(Memory, ref value);
            if ((SPU_IRQ_Address == address) && (((transfer_Control >> 2) & 0x3) != 0)) { 
                SPU_IRQ();
            }
        }

        private (int, int) processReverb(int leftInput, int rightInput) {

            //Apply reverb formula
            //Seems like any Multiplication/Addition needs to be clamped to (-0x8000 , +0x7FFF),
            //not just the last values written to memory


            //  ___Input from Mixer (Input volume multiplied with incoming data)_____________

            int Lin = (vLIN * leftInput) >> 15;
            int Rin = (vRIN * rightInput) >> 15;
            
            //  ____Same Side Reflection (left-to-left and right-to-right)___________________            
              short leftSideReflection = (short)Math.Clamp((((Lin + 
                  ((reverbMemoryRead((uint)dLSAME << 3) * vWALL) >> 15) - 
                  reverbMemoryRead(((uint)mLSAME << 3) - 2)) * vIIR) >> 15) + 
                  reverbMemoryRead(((uint)mLSAME << 3) - 2), -0x8000, +0x7FFF);

              short rightSideReflection = (short)Math.Clamp((((Rin +
                  ((reverbMemoryRead((uint)dRSAME << 3) * vWALL) >> 15) -
                  reverbMemoryRead(((uint)mRSAME << 3) - 2)) * vIIR) >> 15) +
                  reverbMemoryRead(((uint)mRSAME << 3) - 2), -0x8000, +0x7FFF);
            /*
            TODO, handle vIIR bug:
            vIIR works only in range -7FFFh..+7FFFh. When set to -8000h,        
            the multiplication by -8000h is still done correctly, but, 
            the final result (the value written to memory) gets negated
             */
            reverbMemoryWrite(leftSideReflection, (uint)mLSAME << 3);
            reverbMemoryWrite(rightSideReflection, (uint)mRSAME << 3);



            // ___Different Side Reflection (left-to-right and right-to-left)_______________
            short leftSideReflection_d = (short)Math.Clamp((((Lin +
                  ((reverbMemoryRead((uint)dRDIFF << 3) * vWALL) >> 15) -
                  reverbMemoryRead(((uint)mLDIFF << 3) - 2)) * vIIR) >> 15 ) +
                  reverbMemoryRead(((uint)mLDIFF << 3) - 2), -0x8000, +0x7FFF);


            short rightSideReflection_d = (short)Math.Clamp((((Rin +
                  ((reverbMemoryRead((uint)dLDIFF << 3) * vWALL) >> 15) -
                  reverbMemoryRead(((uint)mRDIFF << 3) - 2)) * vIIR) >> 15) +
                  reverbMemoryRead(((uint)mRDIFF << 3) - 2), -0x8000, +0x7FFF);


            reverbMemoryWrite(leftSideReflection_d, (uint)mLDIFF << 3);
            reverbMemoryWrite(rightSideReflection_d, (uint)mRDIFF << 3);



            //  ___Early Echo (Comb Filter, with input from buffer)__________________________ 

            short Lout = (short)Math.Clamp((
                (vCOMB1 * reverbMemoryRead((uint)mLCOMB1 << 3)) >> 15) +
                ((vCOMB2 * reverbMemoryRead((uint)mLCOMB2 << 3)) >> 15) +
                ((vCOMB3 * reverbMemoryRead((uint)mLCOMB3 << 3)) >> 15) +
                ((vCOMB4 * reverbMemoryRead((uint)mLCOMB4 << 3)) >> 15), -0x8000, +0x7FFF);


            short Rout = (short)Math.Clamp((
                (vCOMB1 * reverbMemoryRead((uint)mRCOMB1 << 3)) >> 15) +
                ((vCOMB2 * reverbMemoryRead((uint)mRCOMB2 << 3)) >> 15) +
                ((vCOMB3 * reverbMemoryRead((uint)mRCOMB3 << 3)) >> 15) +
                ((vCOMB4 * reverbMemoryRead((uint)mRCOMB4 << 3)) >> 15), -0x8000, +0x7FFF);
            

            //  ___Late Reverb APF1 (All Pass Filter 1, with input from COMB)________________

            Lout = (short)Math.Clamp(Lout - Math.Clamp(((vAPF1 * reverbMemoryRead(((uint)mLAPF1 << 3) - ((uint)dAPF1 << 3))) >> 15), -0x8000, +0x7FFF), -0x8000, +0x7FFF);
            Rout = (short)Math.Clamp(Rout - Math.Clamp(((vAPF1 * reverbMemoryRead(((uint)mRAPF1 << 3) - ((uint)dAPF1 << 3))) >> 15), -0x8000, +0x7FFF), -0x8000, +0x7FFF);

            reverbMemoryWrite(Lout, (uint)mLAPF1 << 3);
            reverbMemoryWrite(Rout, (uint)mRAPF1 << 3);

            Lout = (short)Math.Clamp(Math.Clamp(((Lout * vAPF1) >> 15),-0x8000,+0x7FFF) + reverbMemoryRead(((uint)mLAPF1 << 3) - ((uint)dAPF1 << 3)), -0x8000, +0x7FFF);
            Rout = (short)Math.Clamp(Math.Clamp(((Rout * vAPF1) >> 15),-0x8000, +0x7FFF) + reverbMemoryRead(((uint)mRAPF1 << 3) - ((uint)dAPF1 << 3)), -0x8000, +0x7FFF);


            //  ___Late Reverb APF2 (All Pass Filter 2, with input from APF1)________________
            Lout = (short)Math.Clamp(Lout - Math.Clamp(((vAPF2 * reverbMemoryRead(((uint)mLAPF2 << 3) - ((uint)dAPF2 << 3))) >> 15), -0x8000,+0x7FFF), -0x8000, +0x7FFF);
            Rout = (short)Math.Clamp(Rout - Math.Clamp(((vAPF2 * reverbMemoryRead(((uint)mRAPF2 << 3) - ((uint)dAPF2 << 3))) >> 15), -0x8000, +0x7FFF), -0x8000, +0x7FFF);

            reverbMemoryWrite(Lout, (uint)mLAPF2 << 3);
            reverbMemoryWrite(Rout, (uint)mRAPF2 << 3);

            Lout = (short)Math.Clamp(Math.Clamp(((Lout * vAPF2) >> 15), -0x8000,+0x7FFF) + reverbMemoryRead(((uint)mLAPF2 << 3) - ((uint)dAPF2 << 3)), -0x8000, +0x7FFF);
            Rout = (short)Math.Clamp(Math.Clamp(((Rout * vAPF2) >> 15), -0x8000,+0x7FFF) + reverbMemoryRead(((uint)mRAPF2 << 3) - ((uint)dAPF2 << 3)), -0x8000, +0x7FFF);

            //  ___Output to Mixer (Output volume multiplied with input from APF2)___________
            int leftOutput = Math.Clamp((Lout * vLOUT) >> 15, -0x8000, +0x7FFF);
            int rightOutput = Math.Clamp((Rout * vROUT) >> 15, -0x8000, +0x7FFF);

            //  ___Finally, before repeating the above steps_________________________________
            reverbCurrentAddress = Math.Max((uint)mBASE << 3, (reverbCurrentAddress + 2) & 0x7FFFE);

            // Wait one 22050Hz cycle, then repeat the above stuff

            return (leftOutput , rightOutput);
        }

        public void SPU_IRQ() {
            if (IRQ9Enable) {
                IRQ_Flag = 1;
                IRQ_CONTROL.IRQsignal(9);
            }
        }
        
        private void reverbMemoryWrite(short value, uint address) {
            if (!reverbEnabled) { return; }     

            address += reverbCurrentAddress;
            uint start = (uint)mBASE << 3;
            uint end = 0x7FFFF;      
            uint final = (start + ((address - start) % (end - start))) & 0x7FFFE; //Aliengment for even addresses only
            if ((final == SPU_IRQ_Address) || ((final + 1) == SPU_IRQ_Address)) { SPU_IRQ(); }
            RAM[final] = (byte)value;
            RAM[final + 1] = (byte)(value >> 8);
        }

        private short reverbMemoryRead(uint address) {
            address += reverbCurrentAddress;
            uint start = (uint)mBASE << 3;
            uint end = 0x7FFFF; 
            uint final = (start + ((address - start) % (end - start))) & 0x7FFFE; //Aliengment for even addresses only
            if((final == SPU_IRQ_Address) || ((final + 1) == SPU_IRQ_Address)) { SPU_IRQ(); }
            return (short)(((uint)RAM[final + 1] << 8) | RAM[final]);
        }

        private void modulatePitch(int i) {
            int step =  voices[i].ADPCM_Pitch;   //Sign extended to 32-bits

            if (((PMON & (1 << i)) != 0) && i > 0) {
                int factor = voices[i - 1].lastSample;
                factor += 0x8000;
                step = step * factor;
                step = step >> 15;
                step = step & 0x0000FFFF;
            }
            if (step > 0x3FFF) { step = 0x4000; }
            voices[i].pitchCounter = (voices[i].pitchCounter + (ushort)step);
        }

        public void playAudio(byte[] samples) {
            bufferedWaveProvider.AddSamples(samples, 0, samples.Length);
            if (waveOutEvent.PlaybackState != PlaybackState.Playing) {
                waveOutEvent.Volume = 1; 
                waveOutEvent.Play();
            }
        }

        internal void DMAtoSPU(uint data) {
            if ((transfer_Control >> 1 & 7) != 2) { throw new Exception(); }
            currentAddress &= 0x7FFFF;
            if((currentAddress <= SPU_IRQ_Address) && ((currentAddress + 3) >= SPU_IRQ_Address)) { SPU_IRQ(); }

            RAM[currentAddress++] = (byte)(data & 0xFF);
            RAM[currentAddress++] = (byte)((data >> 8) & 0xFF);
            RAM[currentAddress++] = (byte)((data >> 16) & 0xFF);
            RAM[currentAddress++] = (byte)((data >> 24) & 0xFF);
        }

        internal uint SPUtoDMA() {
            if ((transfer_Control >> 1 & 7) != 2) { throw new Exception(); }
            currentAddress &= 0x7FFFF;
            if ((currentAddress <= SPU_IRQ_Address) && ((currentAddress + 3) >= SPU_IRQ_Address)) { SPU_IRQ(); }

            uint b0 = RAM[currentAddress++];
            uint b1 = RAM[currentAddress++];
            uint b2 = RAM[currentAddress++];
            uint b3 = RAM[currentAddress++];
       
            return b0 | (b1 << 8) | (b2 << 16) | (b3 << 24);
        }
    }
}
