using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PSXSharp.Shaders {
    public partial class Shader {
        public static readonly string VertixShader = @"
            #version 330 

            layout(location = 0) in ivec2 vertixInput;
            layout(location = 1) in uvec3 vColors;
            layout(location = 2) in vec2 inUV;


            out vec3 color_in;
            out vec2 texCoords;
            flat out ivec2 clutBase;
            flat out ivec2 texpageBase;
         
            uniform int renderMode = 0;
            flat out int renderModeFrag;

            uniform int inClut;
            uniform int inTexpage;

            uniform float display_area_x_start = 0.0f;
            uniform float display_area_y_start = 0.0f;

            uniform float display_area_x_end = 1.0f;
            uniform float display_area_y_end = 1.0f;

            uniform float aspect_ratio_x_offset = 0.0;
            uniform float aspect_ratio_y_offset = 0.0;

            void main() {
    
            //Convert x from [0,1023] and y from [0,511] coords to [-1,1]

            float xpos = ((float(vertixInput.x) + 0.5) / 512.0) - 1.0;
            float ypos = ((float(vertixInput.y) - 0.5) / 256.0) - 1.0;

            //float xpos = ((float(vertixInput.x) / 1024.0) * 2.0) - 1.0;
            //float ypos = ((float(vertixInput.y) / 512.0) * 2.0) - 1.0;

            vec4 positions[4];
            vec2 texcoords[4];
            renderModeFrag = renderMode;

            //TODO: Clean up 

            switch(renderMode){
                 case 0:            
                        gl_Position.xyzw = vec4(xpos,ypos,0.0, 1.0); 
                        texpageBase = ivec2((inTexpage & 0xf) * 64, ((inTexpage >> 4) & 0x1) * 256);
                        clutBase = ivec2((inClut & 0x3f) * 16, inClut >> 6);
                        texCoords = inUV;

                        color_in = vec3(
                        float(vColors.r)/255.0,
                        float(vColors.g)/255.0,
                        float(vColors.b)/255.0);

                        return;

                 case 1:         //16/24bpp vram -> Screen
                 case 2:         
                        positions = vec4[](
                        vec4(-1.0 + aspect_ratio_x_offset, 1.0 - aspect_ratio_y_offset, 1.0, 1.0),    // Top-left
                        vec4(1.0 - aspect_ratio_x_offset, 1.0 - aspect_ratio_y_offset, 1.0, 1.0),     // Top-right
                        vec4(-1.0 + aspect_ratio_x_offset, -1.0 + aspect_ratio_y_offset, 1.0, 1.0),   // Bottom-left
                        vec4(1.0 - aspect_ratio_x_offset, -1.0 + aspect_ratio_y_offset, 1.0, 1.0));   // Bottom-right

                        texcoords = vec2[](		//Inverted in Y because PS1 Y coords are inverted
                        vec2(display_area_x_start, display_area_y_start),   			    // Top-left
                        vec2(display_area_x_end, display_area_y_start),                     // Top-right
                        vec2(display_area_x_start, display_area_y_end),                     // Bottom-left
                        vec2(display_area_x_end, display_area_y_end));                      // Bottom-right

                        break;
            }

          
            texCoords = texcoords[gl_VertexID];
            gl_Position = positions[gl_VertexID];

            return;

  
            }";
    }
}
