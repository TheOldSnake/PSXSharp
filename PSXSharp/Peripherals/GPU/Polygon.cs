using System;
using System.Collections.Generic;
using System.Windows.Forms;
using System.Windows.Media.Media3D;

namespace PSXSharp {
    public unsafe class Polygon : Primitive {
        /*GPU Render Polygon Commands

        First command decoding:
        bit number     value   meaning
           31-29        001    polygon render
           28           1/0    gouraud / flat shading
           27           1/0    4 / 3 vertices
           26           1/0    textured / untextured
           25           1/0    semi-transparent / opaque
           24           1/0    raw texture / modulation
           23-0         rgb    first color value. */

        bool isGouraud;
        bool isQuad;
        bool isTextured;
        bool isSemiTransparent;
        bool isRawTextured;
        int semiTransparency;

        uint[] vertices;
        uint[] colors;
        uint[] uv;
        List<uint> buffer = new List<uint>();
        ushort clut;
        ushort page;
        int numOfParameters = -1;
        int numberOfVertices = 0;
        bool isDithered = false;
        bool setupArrays = false;
        public Polygon(uint value, bool ditherEnabled, ushort globalSemiTransparency) {
            isGouraud = ((value >> 28) & 1) == 1;
            isQuad = ((value >> 27) & 1) == 1;
            isTextured = ((value >> 26) & 1) == 1;
            isSemiTransparent = ((value >> 25) & 1) == 1;
            isRawTextured = ((value >> 24) & 1) == 1;
            numberOfVertices = isQuad ? 4 : 3;
            semiTransparency = isSemiTransparent? globalSemiTransparency : -1;
            vertices = new uint[numberOfVertices];
            colors = new uint[numberOfVertices];
            uv = new uint[numberOfVertices];

            buffer.Add(value);

            numOfParameters = numberOfVertices;     //I hope this works for all polygons

            if (isTextured) {
                numOfParameters += numberOfVertices;
            }
            if (isGouraud) {
                numOfParameters += numberOfVertices;
            }
            else {
                numOfParameters += 1;
            }

            isDithered = ditherEnabled && (isGouraud || !isRawTextured);
        }

        public void Add(uint value) {
            buffer.Add(value);
        }

        public bool IsReady() {
            return buffer.Count == numOfParameters;
        }

        public bool IsTextured() {
            return isTextured;
        }

        public void SetupArrays() {
            int ptr = 0;
            bool onlyOneColor = true;

            if (isGouraud) {
                for (int i = 0; i < numberOfVertices; i++) {
                    colors[i] = buffer[ptr++];
                    vertices[i] = buffer[ptr++];
                    if (isTextured) {
                        uv[i] = buffer[ptr++];
                    }
                }
            } else {
                for (int i = 0; i < numberOfVertices; i++) {
                    colors[i] = buffer[0];
                    if (onlyOneColor) {
                        ptr++;
                        onlyOneColor = false;
                    }
                    vertices[i] = buffer[ptr++];
                    if (isTextured) {
                        uv[i] = buffer[ptr++];
                        if (isRawTextured) {
                            colors[i] = 0x808080;
                        }
                    }
                }
            }
            setupArrays = true;
        }

        public uint GetDrawMode() {
            if (!setupArrays) {
                SetupArrays();
            }
            return (uv[1] >> 16);
        }
   
        public void Draw() {
            if (!setupArrays) {
                SetupArrays();
            }

            clut = (ushort)(uv[0] >> 16);
            page = (ushort)(uv[1] >> 16);

            int textureMode = isTextured? (page >> 7) & 3 : -1;

            if (isSemiTransparent && isTextured) {
                semiTransparency = (page >> 5) & 3;
            }

            Span<short> verticies_span = [
                (short)vertices[0], (short)(vertices[0] >> 16),
                (short)vertices[1], (short)(vertices[1] >> 16),
                (short)vertices[2], (short)(vertices[2] >> 16),
            ];

            ReadOnlySpan<byte> colors_span = [
                (byte)colors[0], (byte)(colors[0] >> 8), (byte)(colors[0] >> 16),
                (byte)colors[1], (byte)(colors[1] >> 8), (byte)(colors[1] >> 16),
                (byte)colors[2], (byte)(colors[2] >> 8), (byte)(colors[2] >> 16),
            ];

            ReadOnlySpan<ushort> uv_span = [
                (ushort)(uv[0] & 0xFF), (ushort)((uv[0] >> 8) & 0xFF),
                (ushort)(uv[1] & 0xFF), (ushort)((uv[1] >> 8) & 0xFF),
                (ushort)(uv[2] & 0xFF), (ushort)((uv[2] >> 8) & 0xFF),
            ];

            GLRenderBackend.DrawTrinangles(verticies_span, colors_span, uv_span, isTextured, clut, page, textureMode, isDithered, semiTransparency);

            if (isQuad) {
                verticies_span = [
                    (short)vertices[1], (short)(vertices[1] >> 16),
                    (short)vertices[2], (short)(vertices[2] >> 16),
                    (short)vertices[3], (short)(vertices[3] >> 16),
                ];

                colors_span = [
                    (byte)colors[1], (byte)(colors[1] >> 8), (byte)(colors[1] >> 16),
                    (byte)colors[2], (byte)(colors[2] >> 8), (byte)(colors[2] >> 16),
                    (byte)colors[3], (byte)(colors[3] >> 8), (byte)(colors[3] >> 16),
                ];

                uv_span = [
                    (ushort)(uv[1] & 0xFF), (ushort)((uv[1] >> 8) & 0xFF),
                    (ushort)(uv[2] & 0xFF), (ushort)((uv[2] >> 8) & 0xFF),
                    (ushort)(uv[3] & 0xFF), (ushort)((uv[3] >> 8) & 0xFF),

                ];

                GLRenderBackend.DrawTrinangles(verticies_span, colors_span, uv_span, isTextured, clut, page, textureMode, isDithered, semiTransparency);
            }
        }
    }
}
