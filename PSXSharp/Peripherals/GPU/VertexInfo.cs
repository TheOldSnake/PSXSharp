using System;
using System.Runtime.InteropServices;

namespace PSXSharp.Peripherals.GPU {

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct VertexInfo {
        //Position
        public Position Position;

        //Colors
        public Color Color;

        //UV
        public UV UV;

        //Per primitive settings
        public int Clut;
        public int TexPage;
        public int TextureMode;
        public int IsDithered;
        public int TransparencyMode;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Position {
        public short X, Y;
        public static Position FromSpan(ReadOnlySpan<short> position) {
            return new Position { X = position[0], Y = position[1] };
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Color {
        public byte R, G, B;
        public static Color FromSpan(ReadOnlySpan<byte> color) {
            return new Color { R = color[0], G = color[1], B = color[2] };
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct UV {
        public ushort U, V;
        public static UV FromSpan(ReadOnlySpan<ushort> uv) {
            return new UV { U = uv[0], V = uv[1] };
        }
    }
}
