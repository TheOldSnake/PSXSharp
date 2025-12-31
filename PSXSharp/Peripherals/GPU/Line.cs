using System;
using System.Collections.Generic;
using System.Linq;

namespace PSXSharp {
    internal class Line : Primitive { 
        bool isGouraud;
        bool isPolyLine;
        bool isSemiTransparent;
        bool isDithered;
        int semiTransparency;
        List<uint> buffer = new List<uint>();
        short[] vertices;
        byte[] colors;

        public Line(uint value, bool isDithered, ushort globalSemiTransparency) {
            isGouraud = ((value >> 28) & 1) == 1;
            isPolyLine = ((value >> 27) & 1) == 1;
            isSemiTransparent = ((value >> 25) & 1) == 1;
            this.isDithered = isDithered;
            semiTransparency = isSemiTransparent? globalSemiTransparency : -1;
            buffer.Add(value);
        }

        public void Add(uint value) {
            buffer.Add(value);
        }

        public bool IsReady() {
            //When polyline mode is active, at least two vertices must be sent to the GPU.
            //The vertex list is terminated by the bits 12-15 and 28-31 equaling 0x5, or (word & 0xF000F000) == 0x50005000.
            if (isPolyLine) {   
                return (buffer.Last() & 0xF000F000) == 0x50005000;
            }
            else {
                return buffer.Count == (isGouraud ? 4 : 3);  
            }
        }

        public void Draw() {

            int numOfVertices = buffer.Count;

            if (isPolyLine) {
                numOfVertices--;
            }
            if (isGouraud) {
                numOfVertices /= 2;
            }
            else {
                numOfVertices--;
            }

            vertices = new short[numOfVertices * 2];
            colors = new byte[numOfVertices * 3];

            int ptr = 0;
            int step = isGouraud ? 2 : 0;

            for (int i = 0; i < colors.Length; i += 3) {
                colors[i] = (byte)buffer[ptr];
                colors[i + 1] = (byte)(buffer[ptr] >> 8);
                colors[i + 2] = (byte)(buffer[ptr] >> 16);
                ptr += step;
            }

            ptr = 1;
            step = isGouraud ? 2 : 1;

            for (int i = 0; i < vertices.Length; i += 2) {
                vertices[i] = (short)buffer[ptr];
                vertices[i + 1] = (short)(buffer[ptr] >> 16);
                ptr += step;
            }

            GLRenderBackend.DrawLines(vertices, colors, isPolyLine, isDithered, semiTransparency);
        }
    }
 }

