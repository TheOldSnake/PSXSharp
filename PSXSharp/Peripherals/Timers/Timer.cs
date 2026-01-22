using System;
namespace PSXSharp.Peripherals.Timers {
    public abstract class Timer {                     
        protected int CurrentValue;
        protected uint Target;
        protected bool Synchronize;                   //(0=Free Run, 1=Synchronize via Bit1-2)
        protected uint SyncMode;                      //Depends on the timer number
        protected bool ResetWhenReachedTarget;        //(0=After Counter=FFFFh, 1=After Counter=Target)
        protected bool IRQWhenReachedTarget;          //(0=Disable, 1=Enable)
        protected bool IRQWhenOverflow;               //(0=Disable, 1=Enable)
        protected bool IRQRepeat;                     //(0=One-shot, 1=Repeatedly)                         ----> Not Implemented 
        protected bool IRQToggleBit10;                //(0=Short Bit10 = 0 Pulse, 1=Toggle Bit10 on/off)   ----> Not Implemented 
        protected uint ClockSource;                   //Depends on the timer number
        protected bool IRQRequest;                    //(0=Yes, 1=No) (Set after Writing)    (W=1) -----> Reversed 
        protected bool CounterReachedTarget;          //(0=No, 1=Yes) (Reset after Reading)
        protected bool CounterOverflowed;             //(0=No, 1=Yes) (Reset after Reading)

        protected ulong ReadCycle;
        protected bool IsPaused;
        public Range Range;

        protected bool ShouldReset = false;

        //Writing 16-bit will write a full 32-bit, according to "Unpredictable Things" in PSX-SPX
        public void WriteHalf(uint address, ushort value) => WriteWord(address, value);
        public ushort ReadHalf(uint address) {     
            uint word = ReadWord(address);
            if ((address & 0x2) == 1) {
                word >>= 16;
            }

            return (ushort)word;
        }

        public uint ReadWord(uint address) {
           
            switch (address & 0xF) {
                case 0:
                    LazyUpdate();
                    uint temp = (uint)CurrentValue;

                    if (ShouldReset) {
                        Reset();                     
                        ScheduleTargetEvent();
                        ShouldReset = false;
                    }

                    return temp;

                case 4: return ReadMode();
                case 8: return Target;
                default: throw new Exception("Unknown Timer Address:" + address.ToString("x"));
            }
        }

        public unsafe void WriteWord(uint address, uint value) {
            /*
             Writing a Current value larger than the Target value will not trigger the condition of Mode Bit4, 
             but make the counter run until FFFFh and wrap around to 0000h once, before using the target value.
            */
            //Console.WriteLine("@" + address.ToString("x") + " : " + value.ToString("x") + " - Timer " + ((address >> 4) & 0xF));
     
            switch (address & 0xF) {
                case 0: 
                    CurrentValue = (int)value;

                    //Schedule IRQ if needed
                    if (CurrentValue < Target) {
                        if (!IsPaused) {
                            if (IRQWhenReachedTarget || ResetWhenReachedTarget) {
                                FlushTimerEvents();
                                ScheduleTargetEvent();
                            } else if (IRQWhenOverflow) {
                                FlushTimerEvents();
                                ScheduleOverflowEvent();
                            }
                        }
                    }
                    break;

                case 4: ConfigureTimer(value); break;
                case 8: 
                    Target = value;     
                    break;
                default: throw new Exception("Unknown Timer Address:" + address.ToString("x"));
            }
        }

        protected void ConfigureTimer(uint mode) {
            Synchronize = (mode & 1) == 1;
            SyncMode = (mode >> 1) & 3;
            ResetWhenReachedTarget = ((mode >> 3) & 1) == 1;
            IRQWhenReachedTarget = ((mode >> 4) & 1) == 1;
            IRQWhenOverflow = ((mode >> 5) & 1) == 1;
            IRQRepeat = ((mode >> 6) & 1) == 1;
            IRQToggleBit10 = ((mode >> 7) & 1) == 1;
            ClockSource = (mode >> 8) & 3;
            IRQRequest = false;             //W=1? I assume on any write it will be reset to false (=1)
            Reset();                        //Writing the mode will force reset the current value
            IsPaused = false;

            Synchronization();

            //Schedule IRQ if needed
            if (!IsPaused) {
                if (IRQWhenReachedTarget) {
                    FlushTimerEvents();
                    ScheduleTargetEvent();
                } else if (IRQWhenOverflow) {
                    FlushTimerEvents();
                    ScheduleOverflowEvent();
                }
            }
        }

        protected uint ReadMode() {
            uint mode = 0;
            mode |= (uint)(Synchronize ? 1 : 0);
            mode |= SyncMode << 1;
            mode |= (uint)(ResetWhenReachedTarget ? 1 : 0) << 3;
            mode |= (uint)(IRQWhenReachedTarget ? 1 : 0) << 4;
            mode |= (uint)(IRQWhenOverflow ? 1 : 0) << 5;
            mode |= (uint)(IRQRepeat ? 1 : 0) << 6;
            mode |= (uint)(IRQToggleBit10 ? 1 : 0) << 7;
            mode |= ClockSource << 8;
            mode |= (uint)((!IRQRequest) ? 1 : 0) << 10;
            mode |= (uint)(CounterReachedTarget ? 1 : 0) << 11;     //(Reset after Reading)
            mode |= (uint)(CounterOverflowed ? 1 : 0) << 12;        //(Reset after Reading)
            CounterReachedTarget = CounterOverflowed = false;
            return mode;
        }

        protected abstract void Reset();
        protected abstract void Tick(int cycles);
        public abstract void LazyUpdate();

        public abstract void SystemClockTick(int cycles);
        public abstract void ScheduleTargetEvent();
        public abstract void ScheduleOverflowEvent();

        public abstract void TimerEventHandler(bool overflow);
        public abstract void ReachedTarget();
        public abstract void Overflowed();

        public abstract void FlushTimerEvents();
        public abstract void Synchronization();
    }
}
