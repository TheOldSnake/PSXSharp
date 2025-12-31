namespace PSXSharp.Shaders {
    public partial class Shader {
        public static readonly string VertexShader = @"
            #version 460 

            //Current drawing mode constants
            const int RENDER_PRIM = 0;
            const int RENDER_VRAM_16BPP = 1;
            const int RENDER_VRAM_24BPP = 2;
         
            //Per primitive settings
            layout(location = 0) in ivec2 vertixInput;
            layout(location = 1) in vec3 vColors;   //Normalized floats
            layout(location = 2) in vec2 inUV;      //Non-normalized floats
            layout(location = 3) in int inClut;
            layout(location = 4) in int inTexpage;
            layout(location = 5) in int inTextureMode;
            layout(location = 6) in int inIsDithered;
            layout(location = 7) in int inTransparencyMode;

            //Outputs to fragment shader
            out vec3 vertexColor;
            out vec2 texCoords;
            flat out ivec2 clutBase;
            flat out ivec2 texpageBase;
            flat out int textureMode;
            flat out int isDithered;
            flat out int transparencyMode;
            flat out int maskBitSetting;
            flat out int renderModeFrag;

            //Settings for drawing the whole frame
            uniform int renderMode = 0;

            uniform float display_area_x_start = 0.0f;
            uniform float display_area_y_start = 0.0f;

            uniform float display_area_x_end = 1.0f;
            uniform float display_area_y_end = 1.0f;

            uniform float aspect_ratio_x_offset = 0.0;
            uniform float aspect_ratio_y_offset = 0.0;
           
            vec4 handleAspectRatio(int id) {
                vec2 xy[4] = vec2[4](
                    vec2(-1,  1),
                    vec2( 1,  1),
                    vec2(-1, -1),
                    vec2( 1, -1)
                );

                vec2 p = xy[id];
                p.x *= 1.0 - aspect_ratio_x_offset;
                p.y *= 1.0 - aspect_ratio_y_offset;

                return vec4(p, 1.0, 1.0);
            }
            
           vec2 handleDisplayArea(int id){
               //gl_VertexID encodes the quad corner as bits:
               //bit 0 (1): 0 = left, 1 = right
               //bit 1 (2): 0 = top,  1 = bottom

               //Note: This inverted in Y because PS1 Y coords are inverted
               float x = ((id & 1) != 0) ? display_area_x_end : display_area_x_start;
               float y = ((id & 2) != 0) ? display_area_y_end : display_area_y_start;
               return vec2(x, y) * vec2(1024.0, 512.0);
           }

            void main() {
  
               //Convert x from [0,1023] and y from [0,511] coords to [-1,1]
               float xpos = ((float(vertixInput.x) + 0.5) / 512.0) - 1.0;
               float ypos = ((float(vertixInput.y) - 0.5) / 256.0) - 1.0;

               vec2 texcoords[4];
               renderModeFrag = renderMode; //Pass rendermode 
            
               if(renderMode == RENDER_PRIM){
                   //Pass the flat outs
                   gl_Position.xyzw = vec4(xpos, ypos, 0.0, 1.0); 
                   texpageBase = ivec2((inTexpage & 0xf) * 64, ((inTexpage >> 4) & 0x1) * 256);
                   clutBase = ivec2((inClut & 0x3f) * 16, inClut >> 6);
                   texCoords = inUV;
                   vertexColor = vColors.rgb;                       
                   textureMode = inTextureMode;
                   isDithered = inIsDithered;
                   transparencyMode = inTransparencyMode;
                } else {
                   //16/24bpp vram -> Screen
                   //Set up the position and UV to draw the vram texture
                   gl_Position = handleAspectRatio(gl_VertexID);
                   texCoords = handleDisplayArea(gl_VertexID);
                }
        }";
    }
}
