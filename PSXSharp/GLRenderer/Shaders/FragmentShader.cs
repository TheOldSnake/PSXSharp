namespace PSXSharp.Shaders {
    public partial class Shader {
        public static readonly string FragmentShader = @"
         #version 460 

         //Current drawing mode constants
         const int RENDER_PRIM       =  0;
         const int RENDER_VRAM_16BPP =  1;
         const int RENDER_VRAM_24BPP =  2;

         //Texture mode constants
         const int NO_TEXTURE        = -1;
         const int TEXTURE_4BPP      =  0;
         const int TEXTURE_8BPP      =  1;
         const int TEXTURE_16BPP     =  2;

         //Vram texture 
         uniform sampler2D u_vramTex;

         //Inputs from vertex shader
         in vec3 vertexColor;
         in vec2 texCoords;
         flat in ivec2 clutBase;
         flat in ivec2 texpageBase;
         flat in int textureMode;
         flat in int isDithered;
         flat in int transparencyMode;  
         flat in int renderModeFrag;
        
         //Global settings
         uniform int maskBitSetting;                    
         uniform ivec4 u_texWindow;                     
         uniform int isCopy = 0;     //-unused-
         
         //Outputs
         layout(location = 0, index = 0) out vec4 outputColor;
         layout(location = 0, index = 1) out vec4 outputBlendColor;
  
         //Dithering constants
         mat4 ditheringTable = mat4(
            -4,  0, -3,  1,
             2, -2,  3, -1,
            -3,  1, -4,  0,
             3, -1,  2, -2
         );

         ivec2 getCurrentLocation(){
            return ivec2(gl_FragCoord.xy);
         }

         vec3 dither(vec3 colors, ivec2 position) {
            // % 4
            int x = position.x & 3;
            int y = position.y & 3;
            int ditherOffset = int(ditheringTable[y][x]);

            colors = (colors * vec3(255.0)) + vec3(ditherOffset);

            //Clamping to [0,255] (or [0,1]) is automatically done because 
            //the frame buffer format is of a normalized fixed-point (RGB5A1)

            return colors / vec3(255.0);
         }

         vec4 grayScale(vec4 color) {
            float x = 0.299 * (color.r) + 0.587 * (color.g) + 0.114 * (color.b);
            return vec4(x,x,x,1);
         }

         int floatToU5(float f) {				
            return int(floor(f * 31.0 + 0.5));
         }

         int floatToU8(float f) {				
            return int(floor(f * 255.0 + 0.5));
         }

         vec4 sampleVRAM(ivec2 coords) {
            coords &= ivec2(1023, 511); //Out-of-bounds VRAM accesses should wrap
            return texelFetch(u_vramTex, coords, 0);
         }

         int sample16(ivec2 coords) {
            vec4 color = sampleVRAM(coords);
            int r = floatToU5(color.r);
            int g = floatToU5(color.g);
            int b = floatToU5(color.b);
            int msb = int(ceil(color.a)) << 15;
            return r | (g << 5) | (b << 10) | msb;
         }

          vec3 texBlend(vec3 color1, vec3 color2) {
            //Blending formula from PSX-SPX:
            //finalChannel.rgb = (texel.rgb * vertexColour.rgb) / vec3(128.0)

            const float denominator = 128.0 / 255.0;
            vec3 ret = (color1 * color2) / vec3(denominator);
            return ret;
         }

         vec4 handleTransparency(int textureMode, float alpha) {
            //Non transparent pixel
            if(textureMode != NO_TEXTURE && alpha == 0){ 
                return vec4(1.00, 1.00, 1.00, 0.00); 
            } 

            switch (transparencyMode) {
                case -1: return vec4(1.00, 1.00, 1.00, 0.00); //(B * 0) + F  ==> Blending Disabled               
                case  0: return vec4(0.50, 0.50, 0.50, 0.50);  //B/2 + F/2
                case  1: return vec4(1.00, 1.00, 1.00, 1.00);  //B + F                      
                case  3: return vec4(0.25, 0.25, 0.25, 1.00);  //B + F/4     

                case  2:                                      //B - F (Hack: handle it manually here)
                     vec4 b = sampleVRAM(getCurrentLocation());
                     outputColor.rgb = b.rgb - outputColor.rgb;
                     return vec4(1.00, 1.00, 1.00, 0.00);                    
            }
         }

         void handleMaskBit(){
           bool forceBit15 = (maskBitSetting & 1) == 1;
           bool checkOldMask = ((maskBitSetting >> 1) & 1) == 1;
            
           if(forceBit15){
               outputColor.a = 1.0;
           }

           if(checkOldMask){
               int oldPixel = sample16(getCurrentLocation());
               int oldPixelBit15 = (oldPixel >> 15) & 1;
               if(oldPixelBit15 == 1){
                  discard;
               }
           }
         }

         vec4 handle24bpp(ivec2 coords){
              //Each 6 bytes (3 shorts) contain two 24bit pixels.
              //Step 1.5 short for each x since 1 24bits = 3/2 shorts 

              int xx = ((coords.x << 1) + coords.x) >> 1; //xx = int(coords.x * 1.5)

             //Ignore reading out of vram
              if(xx > 1022 || coords.y > 511){ 
                  return vec4(0.0f); 
              } 
         
              int p0 = sample16(ivec2(xx, coords.y));
              int p1 = sample16(ivec2(xx + 1, coords.y));
      
              vec4 color; 
              if ((coords.x & 1) != 0) {         
                  color.r = (p0 >> 8) & 0xFF;
                  color.g = p1 & 0xFF;
                  color.b = (p1 >> 8) & 0xFF;
              } else {
                  color.r = p0 & 0xFF;
                  color.g = (p0 >> 8) & 0xFF;
                  color.b = (p1 & 0xFF);
              } 

             return color / vec4(255.0f);   
         }

         ivec2 getColorCoord4BPP(ivec2 UV){
             ivec2 texelCoord = ivec2(UV.x >> 2, UV.y) + texpageBase;
   
             int clutEntry = sample16(texelCoord);
             int shift = (UV.x & 3) << 2;
             int clutIndex = (clutEntry >> shift) & 0xf;

             return ivec2(clutBase.x + clutIndex, clutBase.y);
         }

         ivec2 getColorCoord8BPP(ivec2 UV){
             ivec2 texelCoord = ivec2(UV.x >> 1, UV.y) + texpageBase;
    
             int clutEntry = sample16(texelCoord);
             int shift = (UV.x & 1) << 3;
             int clutIndex = (clutEntry >> shift) & 0xff;

             return ivec2(clutBase.x + clutIndex, clutBase.y);
         }

         ivec2 getColorCoord16BPP(ivec2 UV){
             return UV + texpageBase;
         }

         vec4 handleTexture(){
            //Fix up UVs and apply texture window
            ivec2 UV = ivec2(floor(texCoords + vec2(0.0001, 0.0001))) & ivec2(0xFF);
            UV = (UV & ~(u_texWindow.xy * 8)) | ((u_texWindow.xy & u_texWindow.zw) * 8); //XY contain Mask, ZW contain Offset  

            //Get the color position based on the BPP
            ivec2 colorCoord;
            switch(textureMode){
                case TEXTURE_4BPP:  colorCoord = getColorCoord4BPP(UV);  break;
                case TEXTURE_8BPP:  colorCoord = getColorCoord8BPP(UV);  break;
                case TEXTURE_16BPP: colorCoord = getColorCoord16BPP(UV); break;
            }
                
            //Fetch the color from vram
            vec4 texColor = texelFetch(u_vramTex, colorCoord, 0);

            //On the PSX, texture color 0000h is fully-transparent
            if (texColor == vec4(0.0)) { 
                discard; 
            }

            //Blend texture color with input vertex color
            texColor.rgb = texBlend(texColor.rgb, vertexColor);           
            return texColor;
         }

         void main(){
             //If we're drawing the whole vram:
             switch(renderModeFrag){
                 case RENDER_VRAM_16BPP: outputColor.rgba = sampleVRAM(ivec2(texCoords)); return;
                 case RENDER_VRAM_24BPP: outputColor.rgba = handle24bpp(ivec2(texCoords)); return;
             }
     
            //Otherwise, we're drawing a primitive:
            outputColor = textureMode == NO_TEXTURE ? vec4(vertexColor, 0.0) : handleTexture();

            //Handle transparency
            outputBlendColor = handleTransparency(textureMode, outputColor.a);

            //Handle mask bit setting
            handleMaskBit();

            //Handle dithering 
            if(isDithered == 1){    
                outputColor.rgb = dither(outputColor.rgb, getCurrentLocation());
            }           
        }";
    }
}
