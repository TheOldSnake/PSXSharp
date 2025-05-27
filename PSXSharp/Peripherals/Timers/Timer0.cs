using PSXSharp.Core;
using System;

namespace PSXSharp.Peripherals.Timers {
    public class Timer0 : Timer {           
        bool GotHblank = false;
        bool switchToFreeRun = false;
        public Action ReachedTargerCallback;
        public Action OverflowedCallback;

        public Timer0() {
            Range = new Range(0x1F801100, 12);
            ReachedTargerCallback = ReachedTarget;
            OverflowedCallback = Overflowed;
        }

        protected override void Tick(int cycles) {
            if (IsPaused) { return; }
            if (CurrentValue >= Target) {
                CounterReachedTarget = true;
                if (IRQWhenReachedTarget) {
                    IRQRequest = true;
                    IRQ_CONTROL.IRQsignal(4);
                }
                if (ResetWhenReachedTarget) {   //Shouldn't be instant
                    Reset();
                }
            } else if (CurrentValue >= 0xFFFF) {
                CounterOverflowed = true;
                Reset();
                if (IRQWhenOverflow) {
                    IRQRequest = true;
                    IRQ_CONTROL.IRQsignal(4);
                }
            }

            CurrentValue += cycles;
        }

        public override void SystemClockTick(int cycles) {
            if (ClockSource == 0 || ClockSource == 2) {
                Tick(1);
            }
        }

        public void DotClock() {
            if (ClockSource == 1 || ClockSource == 3) {
                Tick(1);
            }
        }

        public void HblankTick() {
            if (Synchronize) {
                switch (SyncMode) {
                    case 0: Console.WriteLine("[Timer0] Unimplemented Pause During Hblank"); break;
                    case 1:
                    case 2: Reset(); break;
                    case 3: GotHblank = true; break;
                }
            }
        }
        public void HblankOut() {
            if (Synchronize) {
                if (SyncMode == 3) {
                    if (GotHblank) {
                        IsPaused = false;
                        Synchronize = false;
                    } else {
                        IsPaused = true;
                    }
                }
                GotHblank = false;
            }
        }
        public override void ReachedTarget() {
            TimerEventHandler(false);
        }

        public override void Overflowed() {
            TimerEventHandler(true);
        }

        public override void TimerEventHandler(bool overflow) {

            IRQ_CONTROL.IRQsignal(4);
            IRQRequest = true;

            if (overflow) {
                CounterOverflowed = true;
            } else {
                CounterReachedTarget = true;
            }


            if (switchToFreeRun) {
                Synchronize = false;
                IsPaused = false;
            }

            if (!overflow && IRQRepeat && IRQRequest) {
                ScheduleTargetEvent();
            }

            //The Synchronization Modes for timers 0 and 1 are not implemented properly 
            //For timer 0 we should somehow sync to hblank (but the source can be sys clock or dot clock)
            //We can possibly schedule another callback that happens after 1 hblank for this

            if (Synchronize) {
                switch (SyncMode) {
                    case 0: IsPaused = true; break;
                    case 1: Reset(); break;
                    case 2: Reset(); IsPaused = true; break;
                    case 3:
                        switchToFreeRun = true;
                        break;
                }
            }
        }

        public override void ScheduleTargetEvent() {
            if (ClockSource == 0 || ClockSource == 2) {
                Scheduler.ScheduleEvent((int)Target, ReachedTargerCallback, Event.Timer0);
            } else {
                //Dotclock -- TODO
            }
        }

        public override void ScheduleOverflowEvent() {
            if (ClockSource == 0 || ClockSource == 2) {
                Scheduler.ScheduleEvent(0xFFFF, OverflowedCallback, Event.Timer0);
            } else {
                //Dotclock -- TODO
            }
        }

        protected override void Reset() {
            CurrentValue = 0;
            CounterReachedTarget = false;
            CounterOverflowed = false;
        }

        public override void LazyUpdate() {
            if (IsPaused) {
                return;
            }

            //Calculate how many cycles have passed 
            ulong cpuCurrentCycle = CPUWrapper.GetCPUInstance().GetCurrentCycle();
            int diff = (int)(cpuCurrentCycle - ReadCycle);

            //We need to handle the dot clock...
            /*if (ClockSource == 1 || ClockSource == 3) {
                diff /= ... ;
            }*/

            if ((CurrentValue > Target && ResetWhenReachedTarget) || CurrentValue > 0xFFFF) {
                Reset();
            }

            CurrentValue += diff;
            ReadCycle = cpuCurrentCycle;
        }

        public override void FlushTimerEvents() {
            Scheduler.FlushEvents(Event.Timer0);
        }

        public override void Synchronization() {
            //TODO
        }
    }
}