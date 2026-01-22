namespace PSXSharp {
    public struct Range {
        public uint Start;
        public uint Length;
        public readonly bool Contains(uint address) => address >= Start && address < Start + Length;
        public Range(uint start, uint length) {
            Start = start;
            Length = length;
        }
    }
}
