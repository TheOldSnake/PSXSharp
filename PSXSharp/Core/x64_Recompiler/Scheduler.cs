using System;
using System.Collections.Generic;

namespace PSXSharp.Core.x64_Recompiler {
    public static class Scheduler {       
        private static List<ScheduledEvent> ScheduledEvents = new List<ScheduledEvent>();
        private static ScheduledEvent CPUHeldEvent;
        private static Action DummyCallback = Dummy;

        public static void ScheduleEvent(int delayCycles, Action callback, Event type) {
            ulong currentTime = CPUWrapper.GetCPUInstance().GetCurrentCycle();

            ScheduledEvent scheduledEvent = new ScheduledEvent();
            scheduledEvent.Callback = callback;
            scheduledEvent.Type = type;
            scheduledEvent.EndTime = currentTime + (ulong)delayCycles;
            ScheduledEvents.Add(scheduledEvent);

            //Sort the list in ascending order of end time
            ScheduledEvents.Sort((a, b) => a.EndTime.CompareTo(b.EndTime));
        }

        public static void ScheduleEvent(int delayCycles, Action callback, Event type, ulong currentCycle) {
            ScheduledEvent scheduledEvent = new ScheduledEvent();
            scheduledEvent.Callback = callback;
            scheduledEvent.Type = type;
            scheduledEvent.EndTime = currentCycle + (ulong)delayCycles;
            ScheduledEvents.Add(scheduledEvent);

            //Sort the list in ascending order of end time
            ScheduledEvents.Sort((a, b) => a.EndTime.CompareTo(b.EndTime));
        }

        public static ScheduledEvent DequeueNearestEvent() {
            ScheduledEvent nearest = ScheduledEvents[0];
            ScheduledEvents.RemoveAt(0);
            CPUHeldEvent = nearest;
            return nearest;
        }

        public static void FlushEvents(Event type) {
            for (int i = ScheduledEvents.Count - 1; i >= 0; i--) {
                if (ScheduledEvents[i].Type == type) {
                    ScheduledEvents.RemoveAt(i);
                }
            }

            //If the CPU is holding an event that it flushed, we point it to an empty function
            //However, this is not very common
            if (CPUHeldEvent.Type == type) {
                CPUHeldEvent.Callback = DummyCallback;
            }
        }

        public static void FlushAllEvents() {
            ScheduledEvents.Clear();
            CPUHeldEvent = null;
        }

        public static bool HasEventOfType(Event type) {
            foreach (ScheduledEvent scheduledEvent in ScheduledEvents) {
                if (scheduledEvent.Type == type) {
                    return true;
                }
            }
            return false;
        }

        public static int HowManyEventOfType(Event type) {
            int numberOfEvents = 0;
            foreach (ScheduledEvent scheduledEvent in ScheduledEvents) {
                if (scheduledEvent.Type == type) {
                    numberOfEvents++;
                }
            }
            return numberOfEvents;
        }

        public static void Dummy() { }
    }

    public class ScheduledEvent {
        public Action Callback;            //Event Handler
        public Event Type;                 //Event Type
        public ulong EndTime;              //Time of which the event should happen
    }

    public enum Event {
        Vblank,
        Hblank,
        SPU,
        CDROM_General,
        CDROM_ReadOrPlay,
        DMA,
        Timer0,
        Timer1,
        Timer2,
        SIO,
    }
}
