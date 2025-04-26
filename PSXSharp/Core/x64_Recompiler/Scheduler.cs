using System.Collections.Generic;

namespace PSXSharp.Core.x64_Recompiler {

    //Not complete. 
    //TODO
    public static class Scheduler {       
        public static List<ScheduledIRQ> ScheduledIRQs = new List<ScheduledIRQ>();
        public static void ScheduleIRQ(uint cycles, uint IRQ) {
            ScheduledIRQ scheduledIRQ = new ScheduledIRQ();
            scheduledIRQ.Cycles = cycles;
            scheduledIRQ.IRQ = IRQ;
            ScheduledIRQs.Add(scheduledIRQ);

            //Sort the list in ascending order of Cycles
            ScheduledIRQs.Sort((a, b) => a.Cycles.CompareTo(b.Cycles));
        }

        public static void Tick(uint cycles, ref BUS bus) {
            foreach (ScheduledIRQ ScheduledIRQ in ScheduledIRQs) {
                ScheduledIRQ.Cycles -= cycles;
            }
            bus.Tick((int)cycles);
        }

        public static uint HowCyclesUntilIRQ() {
            return ScheduledIRQs.Count > 0 ? ScheduledIRQs[0].Cycles : 200;
        }

        public static bool ShouldInterrupt() {
            return ScheduledIRQs.Count > 0 && ScheduledIRQs[0].Cycles <= 0;
        }
    }

    public class ScheduledIRQ {
        public uint Cycles;
        public uint IRQ;
    }

}
