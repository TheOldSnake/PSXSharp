using System;
using System.Collections.Generic;

namespace PSXSharp.Core {
    public static class Scheduler {
        public static List<ScheduledEvent> ScheduledEvents = [];
        private static ulong CurrentTime => CPUWrapper.GetCPUInstance().GetCurrentCycle();
        public static int EventsCount => ScheduledEvents.Count;

        public static void ScheduleEvent(int delayCycles, Action callback, Event type) {
            ulong endTime = CurrentTime + (ulong)delayCycles;
            ScheduledEvent scheduledEvent = new ScheduledEvent(endTime, callback, type);
            InsertAndSort(scheduledEvent);
        }

        public static void ScheduleInitialEvent(int delayCycles, Action callback, Event type) {
            //Here CurrentTime is assumed to be 0
            ScheduledEvent scheduledEvent = new ScheduledEvent((ulong)delayCycles, callback, type);
            InsertAndSort(scheduledEvent);
        }

        private static void InsertAndSort(ScheduledEvent scheduledEvent) {
            ScheduledEvents.Add(scheduledEvent);

            //Sort the list in ascending order of end time
            ScheduledEvents.Sort((a, b) => a.EndTime.CompareTo(b.EndTime)); 
        }

        public static void FlushEvents(Event type) {
            for (int i = ScheduledEvents.Count - 1; i >= 0; i--) {
                if (ScheduledEvents[i].Type == type) {
                    ScheduledEvents.RemoveAt(i);
                }
            }
        }

        public static void FlushAllEvents() {
            ScheduledEvents.Clear();
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

        private static bool IsGPUEvent(Event type) {
            return type == Event.Vblank || type == Event.Hblank;
        }

        private static bool IsTimerEvent(Event type) {
            return type == Event.Timer0 || type == Event.Timer1 || type == Event.Timer2;
        }
    }

    public class ScheduledEvent {
        public Action Callback;            //Event Handler
        public Event Type;                 //Event Type
        public ulong EndTime;              //Time of which the event should happen

        public ScheduledEvent(ulong endTime, Action callback, Event type) {
            EndTime = endTime;
            Callback = callback;
            Type = type;
        }
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
        ImmediateIRQ,
    }
}
