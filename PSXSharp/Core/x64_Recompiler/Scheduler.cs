using System;
using System.Collections.Generic;

namespace PSXSharp.Core.x64_Recompiler {
    public static class Scheduler {
        private static List<ScheduledEvent> ScheduledEvents = [];
        private static ScheduledEvent CPUHeldEvent;
        private static ulong CurrentTime => CPUWrapper.GetCPUInstance().GetCurrentCycle();
        public static int EventsCount => ScheduledEvents.Count;

        public static void ScheduleEvent(int delayCycles, Action callback, Event type) {
            ulong endTime = CurrentTime + (ulong)delayCycles;
            ScheduledEvent scheduledEvent = new ScheduledEvent(endTime, callback, type);

            //If the incoming event is sooner than the event held by the CPU then switch to it and re-add the other one
            if (CPUHeldEvent != null && scheduledEvent.EndTime < CPUHeldEvent.EndTime) {
                SwapCPUHeldEvent(scheduledEvent);
                return;
            }

            InsertAndSort(scheduledEvent);
        }

        public static void ScheduleInitialEvent(int delayCycles, Action callback, Event type) {
            //Here CurrentTime is assumed to be 0
            ScheduledEvent scheduledEvent = new ScheduledEvent((ulong)delayCycles, callback, type);
            InsertAndSort(scheduledEvent);
        }

        public static ScheduledEvent DequeueNearestEvent() {
            ScheduledEvent nearest = ScheduledEvents[0];
            ScheduledEvents.RemoveAt(0);
            CPUHeldEvent = nearest;
            return nearest;
        }

        private static void SwapCPUHeldEvent(ScheduledEvent soonerEvent) {
            //Deep copy
            ScheduledEvent oldCPUHeldEvent = new ScheduledEvent(CPUHeldEvent.EndTime, CPUHeldEvent.Callback, CPUHeldEvent.Type);
            InsertAndSort(oldCPUHeldEvent);

            CPUHeldEvent.Callback = soonerEvent.Callback;
            CPUHeldEvent.Type = soonerEvent.Type;
            CPUHeldEvent.EndTime = soonerEvent.EndTime;
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

            //A bit hacky, if the CPU is holding an event that is flushed
            //then overwrite the fields of the object held by the cpu

            if (CPUHeldEvent.Type == type) {
                ScheduledEvent next = ScheduledEvents[0];
                ScheduledEvents.RemoveAt(0);

                //We cannot simply assign CPUHeldEvent = next
                CPUHeldEvent.Callback = next.Callback;
                CPUHeldEvent.Type = next.Type;
                CPUHeldEvent.EndTime = next.EndTime;
            }

            //However, this will cause the CPU to skip interrupt checking because it will continue
            //until the event after the one that got flushed, whenever that is
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
