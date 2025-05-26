namespace PSXSharp {
    public class Response {
        public byte[] values;
        public byte INT;
        public long delay;
        public Response(byte[] values, CD_ROM.Delays delay, CD_ROM.Flags INT) {
            this.values = values;
            this.delay = (long)delay;
            this.INT = (byte)INT;
        }

        public Response(byte[] values, long delay, int INT) {    //Overload
            this.values = values;
            this.delay = delay;
            this.INT = (byte)INT;
        }
    }
}
