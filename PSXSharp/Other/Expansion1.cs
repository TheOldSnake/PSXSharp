namespace PSXSharp {
    public class Expansion1 {
        public Range Range = new Range(0x1F000000, 8192 * 1024);

        //Access Ignored
        public byte ReadByte(uint address) => 0xFF; 
        public void WriteByte(uint address, byte value) {

        }
    }
}
