namespace PSXSharp {
    public class Expansion2 {
        public Range Range = new Range(0x1f802000, 66);
        //Access Ignored
        public byte ReadByte(uint address) => 0xFF;
        public void WriteByte(uint address, byte value) {

        }
    }
}
