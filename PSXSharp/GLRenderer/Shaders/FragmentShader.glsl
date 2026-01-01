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

//Transparency mode constants
const int TRANSPARENCY_DISABLED           = -1; // (B * 0) + F
const int TRANSPARENCY_HALF               =  0; // B/2 + F/2
const int TRANSPARENCY_ADD                =  1; // B + F
const int TRANSPARENCY_REVERSE_SUBTRACT   =  2; // B - F (hacky)
const int TRANSPARENCY_QUARTER            =  3; // B + F/4

//Blend factors based on the transparency mode
const vec4 BLEND_ZERO        = vec4(1.0, 1.0, 1.0, 0.0);
const vec4 BLEND_HALF        = vec4(0.5, 0.5, 0.5, 0.5);
const vec4 BLEND_ONE         = vec4(1.0, 1.0, 1.0, 1.0);
const vec4 BLEND_QUARTER     = vec4(0.25, 0.25, 0.25, 1.0);

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

vec3 dither(vec3 colors) {
    if(isDithered == 0){ return colors; }

    ivec2 position = getCurrentLocation();
    
    // % 4
    int x = position.x & 3;
    int y = position.y & 3;
    float ditherOffset = float(ditheringTable[y][x]) / 255.0; //Normalize the offset

    colors += vec3(ditherOffset);

    //Clamping to [0,255] (or [0,1]) is automatically done because 
    //the frame buffer format is of a normalized fixed-point (RGB5A1)
    return colors;
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

    const float denominator = 128.0 / 255.0;    //Normalize the denominator
    vec3 ret = (color1 * color2) / vec3(denominator);
    return ret;
}

vec4 handleTransparency(int textureMode, float alpha) {
    //Non transparent pixel (bit15 = 0)
    if(textureMode != NO_TEXTURE && alpha == 0){ 
        return BLEND_ZERO; 
    } 

    switch (transparencyMode){
        case TRANSPARENCY_DISABLED:  return BLEND_ZERO;
        case TRANSPARENCY_HALF:      return BLEND_HALF;
        case TRANSPARENCY_ADD:       return BLEND_ONE;
        case TRANSPARENCY_QUARTER:   return BLEND_QUARTER;

        case TRANSPARENCY_REVERSE_SUBTRACT:
            // B - F (Hack: handle manually)
            vec4 background = sampleVRAM(getCurrentLocation());
            outputColor.rgb = background.rgb - outputColor.rgb;
            return BLEND_ZERO;
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

    int newXCoord = ((coords.x << 1) + coords.x) >> 1; //or just newXCoord = int(coords.x * 1.5)

    //Ignore reading out of vram
    if(newXCoord > 1022 || coords.y > 511){ 
        return vec4(0.0f); 
    } 
 
    //Read 2 pixels
    int p0 = sample16(ivec2(newXCoord, coords.y));
    int p1 = sample16(ivec2(newXCoord + 1, coords.y));
      
    //Interpret the 24-bit color depending on original X being even/odd
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
    int clutIndex = (clutEntry >> shift) & 0xF;

    return ivec2(clutBase.x + clutIndex, clutBase.y);
}

ivec2 getColorCoord8BPP(ivec2 UV){
    ivec2 texelCoord = ivec2(UV.x >> 1, UV.y) + texpageBase;
    
    int clutEntry = sample16(texelCoord);
    int shift = (UV.x & 1) << 3;
    int clutIndex = (clutEntry >> shift) & 0xFF;

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
     //If we're drawing the whole vram to the screen:
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
    outputColor.rgb = dither(outputColor.rgb);
}