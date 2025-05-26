using PSXSharp.Core;
using System;
using static PSXSharp.CD_ROM;

namespace PSXSharp.Peripherals.Timers {
    public class Timer1 : Timer {
        bool switchToFreeRun;
        Action ReachedTargerCallback;
        Action OverflowedCallback;
        const int CPUCyclesPerVblank = 565047;
        const int CPUCyclesPerHblank = 2149;
        public bool IsUsingHblankClk => ClockSource == 1 || ClockSource == 3;
        public Timer1() {
            Range = new Range(0x1F801110, 12);
            ReachedTargerCallback = ReachedTarget;
            OverflowedCallback = Overflowed;
        }

        protected override void Tick(int cycles) {
            if (IsPaused) { return; }
            if (CurrentValue >= Target) {
                CounterReachedTarget = true;
                if (IRQWhenReachedTarget) {
                    IRQRequest = true;
                    IRQ_CONTROL.IRQsignal(5);
                }
                if (ResetWhenReachedTarget) {   //Shouldn't be instant
                    Reset();
                }
            } else if (CurrentValue >= 0xFFFF) {
                CounterOverflowed = true;
                Reset();
                if (IRQWhenOverflow) {
                    IRQRequest = true;
                    IRQ_CONTROL.IRQsignal(5);
                }
            }

            CurrentValue += cycles;
        }

        public override void SystemClockTick(int cycles) {
            if (ClockSource == 0 || ClockSource == 2) {
                Tick(cycles);
            }
        }
        public void HblankTick() {
            if (ClockSource == 1 || ClockSource == 3) {
                Tick(1);
            }
        }

        public void VblankTick() {
            if (Synchronize) {
                switch (SyncMode) {
                    case 0: Console.WriteLine("[Timer1] Unimplemented Pause During Vblank"); break;
                    case 1:
                    case 2: Reset(); break;
                    case 3: 
                        switchToFreeRun = true; 
                        IsPaused = false;
                        Synchronize = false;
                        break;
                }
            }
        }

        public void VblankOut() {
            if (Synchronize) {
                if (SyncMode == 3) {
                    if (switchToFreeRun) {
                        IsPaused = false;
                        Synchronize = false;
                    } else {
                        IsPaused = true;
                    }
                }
                switchToFreeRun = false;
            }
        }

        public override void ReachedTarget() {
            TimerEventHandler(false);
        }

        public override void Overflowed() {
            TimerEventHandler(true);
        }

        public override void TimerEventHandler(bool overflow) {
            //if (IsPaused) { return; }

            IRQ_CONTROL.IRQsignal(5);
            IRQRequest = true;
            //Console.WriteLine("[Timer 1] Reached Target: " + Target.ToString("x")); 

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
            //For timer 1 we should somehow sync to vblank (but the source can be sys clock or hblank)
            //We can possibly schedule another callback that happens after 1 vblank for this

            /*if (Synchronize) {
                switch (SyncMode) {
                    case 0: IsPaused = true; break;
                    case 1: Reset(); break;
                    case 2: Reset(); IsPaused = true; break;
                    case 3:
                        switchToFreeRun = true;
                        break;
                }
            }*/
        }

        public override void ScheduleTargetEvent() {
            if (ClockSource == 0 || ClockSource == 2) {
                Scheduler.ScheduleEvent((int)Target, ReachedTargerCallback, Event.Timer1);
            } else {
                Scheduler.ScheduleEvent((int)Target * CPUCyclesPerHblank, ReachedTargerCallback, Event.Timer1);
            }
        }

        public override void ScheduleOverflowEvent() {
            if (ClockSource == 0 || ClockSource == 2) {
                Scheduler.ScheduleEvent(0xFFFF, OverflowedCallback, Event.Timer1);
            } else {
                Scheduler.ScheduleEvent(0xFFFF * CPUCyclesPerHblank, OverflowedCallback, Event.Timer1);
            }
        }

        protected override void Reset() {
            CurrentValue = 0;
        }

        public override void LazyUpdate() {
            if (IsPaused) {
                return;
            }

            //Calculate how many cycles have passed 
            ulong cpuCurrentCycle = CPUWrapper.GetCPUInstance().GetCurrentCycle();
            int diff = (int)(cpuCurrentCycle - ReadCycle);

            if (ClockSource == 1 || ClockSource == 3) {
                diff /= CPUCyclesPerHblank;
            }

            if (diff > 0) {
                ReadCycle = cpuCurrentCycle;
            }

            if ((CurrentValue > Target && ResetWhenReachedTarget) || CurrentValue > 0xFFFF) {
                Reset();
            }

            CurrentValue += diff;
            //Console.WriteLine(CurrentValue.ToString("x"));
        }

        public override void FlushTimerEvents() {
            Scheduler.FlushEvents(Event.Timer1);
        }

        public override void Synchronization() {
            //TODO
            //For Timer 1:
            //SyncMode 3 = Pause until Vblank occurs once, then switch to free run
            if (SyncMode == 3) {
                IsPaused = true;
            }
        }
    }
}
