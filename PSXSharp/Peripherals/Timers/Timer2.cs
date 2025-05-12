using PSXSharp.Core;
using PSXSharp.Core.x64_Recompiler;
using System;

namespace PSXSharp.Peripherals.Timers {
    public class Timer2 : Timer {
        int delay;
        Action ReachedTargetCallback;
        Action OverflowedCallback;

        public Timer2() {
            Range = new Range(0x1F801120, 12);
            ReachedTargetCallback = ReachedTarget;
            OverflowedCallback = Overflowed;
        }

        protected override void Tick(int cycles) {
            if (IsPaused) { return; }

            if (Synchronize) {
                switch (SyncMode) {
                    case 0:
                    case 3:
                        IsPaused = true;
                        return;

                    case 1:
                    case 2:
                        Synchronize = false;
                        break;
                }
            }

            if (CurrentValue >= Target) {
                CounterReachedTarget = true;
                if (IRQWhenReachedTarget) {
                    IRQRequest = true;
                    IRQ_CONTROL.IRQsignal(6);
                }
                if (ResetWhenReachedTarget) {   //Shouldn't be instant
                    Reset();
                }
            } else if (CurrentValue >= 0xFFFF) {
                CounterOverflowed = true;
                Reset();
                if (IRQWhenOverflow) {
                    IRQRequest = true;
                    IRQ_CONTROL.IRQsignal(6);
                }
            }

            CurrentValue += cycles;
        }

        public override void SystemClockTick(int cycles) {
            switch (ClockSource) {
                case 0:
                case 1:
                    Tick(cycles);
                    break;

                case 2:
                case 3:
                    delay += cycles;
                    if (delay > 8) {
                        delay -= 8;
                        Tick(1);
                    }
                    break;
            }
        }

        public override void ReachedTarget() {
            TimerEventHandler(false);
        }

        public override void Overflowed() {
            TimerEventHandler(true);
        }

        public override void TimerEventHandler(bool overflow) {
            //TODO

            // if (IsPaused) { return; }

            IRQ_CONTROL.IRQsignal(6);
            IRQRequest = true;

            if (overflow) {
                CounterOverflowed = true;
            } else {
                CounterReachedTarget = true;
            }

            if (!overflow && IRQRepeat && IRQRequest) {
                ScheduleTargetEvent();
            }

            //Console.WriteLine("Timer2 IRQ");
        }

        public override void ScheduleTargetEvent() {
            if (Target > 0xFFFF || Target == 0) {
                return;
            }

            if (ClockSource == 0 || ClockSource == 1) {
                Scheduler.ScheduleEvent((int)Target, ReachedTargetCallback, Event.Timer2);
            } else {
                Scheduler.ScheduleEvent((int)Target * 8, ReachedTargetCallback, Event.Timer2);
            }
        }

        public override void ScheduleOverflowEvent() {
            if (ClockSource == 0 || ClockSource == 1) {
                Scheduler.ScheduleEvent(0xFFFF, OverflowedCallback, Event.Timer2);
            } else {
                Scheduler.ScheduleEvent(0xFFFF * 8, OverflowedCallback, Event.Timer2);
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

            if (ClockSource == 2 || ClockSource == 3) {
                diff /= 8;
            }

            if (diff > 0) {
                CurrentValue += diff;
            }

            if ((CurrentValue > Target && ResetWhenReachedTarget) || CurrentValue > 0xFFFF) {
                Reset();
            }
            //Console.WriteLine($"Timer2: {CurrentValue}");
            ReadCycle = cpuCurrentCycle;
        }

        public override void FlushTimerEvents() {
            Scheduler.FlushEvents(Event.Timer2);
        }

        public override void Synchronization() {
            if (Synchronize) {
                switch (SyncMode) {
                    //Pause Now
                    case 0:
                    case 3:
                        IsPaused = true;
                        return;

                    //Free Run
                    case 1:
                    case 2:
                        Synchronize = false;
                        IsPaused = false;
                        break;
                }
            }
        }
    }
}
