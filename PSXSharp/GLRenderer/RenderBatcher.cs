using OpenTK.Graphics.OpenGL;
using PSXSharp.Peripherals.GPU;
using System;

namespace PSXSharp {
    public static class RenderBatcher {
        //Here we buffer the drawing commands in the vertex buffer, and flush when needed

        private static PrimitiveType CurrentBatchType = PrimitiveType.Triangles;
        private const int MAX_VERTICES = 5000;
        private static readonly VertexInfo[] _VertexBuffer = new VertexInfo[MAX_VERTICES];
        private static int VertexInfoIndex = 0;
        public static int CurrentVertexIndex => VertexInfoIndex;    
        public static VertexInfo[] VertexBuffer => _VertexBuffer;

        public static void RenderBatch() {
            if (VertexInfoIndex == 0) { return; }

            //We need to rebind the vertex info buffer first, then draw
            GLRenderBackend.BindVertexInfo();    
            GL.DrawArrays(CurrentBatchType, 0, VertexInfoIndex);
            VertexInfoIndex = 0;
        }

        public static void SetBatchType(PrimitiveType batchType) {
            if (CurrentBatchType != batchType) {
                RenderBatch();
                CurrentBatchType = batchType;
            }
        }

        public static void EnsureEnoughBufferSpace(int numberOfVertices) {
            if (CurrentVertexIndex + numberOfVertices >= MAX_VERTICES) {
                RenderBatch();
            }
        }

        public static void AddVertex(ReadOnlySpan<short> positionSpan, ReadOnlySpan<byte> colorSpan, ReadOnlySpan<ushort> uvSpan,
           int clut, int texPage, int texMode, int isDithered, int transMode) {
            VertexBuffer[VertexInfoIndex++] = new VertexInfo {
                Position = Position.FromSpan(positionSpan),
                Color = Color.FromSpan(colorSpan),
                UV = UV.FromSpan(uvSpan),
                Clut = clut,
                TexPage = texPage,
                TextureMode = texMode,
                IsDithered = isDithered,
                TransparencyMode = transMode,
            };
        }
    }
}
