using OpenTK.Graphics.OpenGL;
using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace PSXSharp {
    internal class Rectangle : Primitive {
        List<uint> buffer = new List<uint>();
        int numOfParameters = -1;
        uint opcode;
        bool isTextured;
        bool isSemiTransparent;
        bool isRawTextured;
        byte size;
        byte R;
        byte G;
        byte B;
        ushort texPage; //Unlike for Textured-Polygons, the "Texpage" must be set up separately for Rectangles, via GP0(E1h)
                        //I also added texmode in texpage (bits 7-8)
        int semiTransparency;
        int texDepth;

        public Rectangle(uint value, ushort texPage, byte globalSemiTransparency, byte texDepth) { 
            //this.numOfParameters = numOfParameters;
            opcode = (value >> 24) & 0xff;
            size = (byte)((value >> 27) & 0x3);
            isTextured = ((value >> 26) & 1) == 1;
            isSemiTransparent = ((value >> 25) & 1) == 1;
            isRawTextured = ((value >> 24) & 1) == 1;
            semiTransparency = isSemiTransparent? globalSemiTransparency : -1;
            this.texPage = texPage;
            this.texDepth = isTextured? texDepth : -1;

            buffer.Add(value);

            this.numOfParameters = 2;
            if (isTextured) {
                this.numOfParameters++;
            }
            if (size == 0) {
                this.numOfParameters++;
            }
            
            /*Console.WriteLine("Rectangle : " + opcode.ToString("x") + " - expected number of parameters: " + this.numOfParameters);
            Console.WriteLine("isTextured: " + isTextured);
            Console.WriteLine("isRawTextured: " + isRawTextured);
            Console.WriteLine("size: " + size);*/
        }

        public void Add(uint value) {
            buffer.Add(value);  
        }
        public bool IsReady() {
            return buffer.Count == numOfParameters;
        }
        public void Draw() {
        
            //...and, of course, the GPU does render Rectangles as a single entity, without splitting them into two triangles.
            //Width and Height can be up to 1023x511

            
            if (isTextured && isRawTextured) {
                R = G = B = 0x80;            //No blend color
            }
            else {
                 R = (byte)buffer[0];
                 G = (byte)(buffer[0] >> 8);
                 B = (byte)(buffer[0] >> 16);
            }
           
            ushort width = 0;
            ushort height = 0;

            switch (size) {
                case 0:                                              //Variable
                    width = (ushort)(buffer[isTextured ? 3 : 2] & 0x3FF);
                    height = (ushort)((buffer[isTextured ? 3 : 2] >> 16) & 0x1FF);
                    break;       

                case 1: width = height = 1;  break;                  //1x1
                case 2: width = height = 8;  break;                  //8x8
                case 3: width = height = 16; break;                  //16x16
            }
            
            short x1 = (short)(buffer[1] & 0x7FF);                   //Upper left 
            short y1 = (short)((buffer[1] >> 16) & 0x7FF);

            short x2 = (short)(x1 + width);                          //Lower right
            short y2 = (short)(y1 + height);

            ushort tx1 = 0;
            ushort ty1 = 0;
            ushort tx2 = 0;
            ushort ty2 = 0;
            ushort clut = 0;

            if (isTextured) {
                tx1 = (ushort)(buffer[2] & 0xFF);          //Texture Upper left 
                ty1 = (ushort)((buffer[2] >> 8) & 0xFF);

                tx2 = (ushort)(tx1 + width);               //Texture Lower right
                ty2 = (ushort)(ty1 + height);

                clut = (ushort)(buffer[2] >> 16);
            }

            //Split to 2 triangles
            Span<short> verticies = [x1, y1,    x2, y1,     x1, y2, 
                                     x2, y1,    x2, y2,     x1, y2];

            ReadOnlySpan<byte> colors = [R, G, B,    R, G, B,    R, G, B,
                                         R, G, B,    R, G, B,    R, G, B];

            ReadOnlySpan<ushort> uv = [tx1, ty1,    tx2, ty1,   tx1, ty2,
                                       tx2, ty1,    tx2, ty2,   tx1, ty2];


           const bool DITHERING = false;    //Rectangles are NOT dithered
           GLRenderBackend.DrawTrinangles(verticies, colors, uv, isTextured, clut, texPage, texDepth, DITHERING, semiTransparency);  
        }

        //Helpers
        public static void WriteRectangleCoords(int x, int y, int width, int height, Span<short> destination) {
            destination[0] = (short)x;
            destination[1] = (short)y;
            destination[2] = (short)(x + width);
            destination[3] = (short)y;
            destination[4] = (short)(x + width);
            destination[5] = (short)(y + height);
            destination[6] = (short)x;
            destination[7] = (short)(y + height);
        }

        public static void WriteRectangleCoords(int x, int y, int width, int height, Span<ushort> destination) {
            destination[0] = (ushort)x;
            destination[1] = (ushort)y;
            destination[2] = (ushort)(x + width);
            destination[3] = (ushort)y;
            destination[4] = (ushort)(x + width);
            destination[5] = (ushort)(y + height);
            destination[6] = (ushort)x;
            destination[7] = (ushort)(y + height);
        }
    }
}
