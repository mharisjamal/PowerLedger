// Liquid glass pass 4 of 4: the url filter's feDisplacementMap. The source map (DisplacementField.SourceMap) names, for
// each device pixel, how far away the box pixel its colour comes from lies, already moved and mirrored as Chromium moves
// it; this pass reads that pixel, nearest, from the blurred backdrop. The move is relative to the pixel, so the pass
// never needs to know where the input's texture starts. The filter works in linearRGB, so its source graphic goes
// through 8 bit linearRGB and back: dark colours come out in coarser steps, as Chromium's do.
// Compiled by scripts/liquid-glass/compile-shaders.ps1 to Displace.ps (ps_2_0).
sampler2D input : register(s0);   // the blurred backdrop: the box plus a margin round it
sampler2D map : register(s1);     // the source map on the same texels
float4 ddxDdy : register(c0);     // WPF's DdxUvDdyUv: one texel across (x) and down (w), where the renderer gives it
float4 texel : register(c1);      // xy: one texel from the input's size, for the software renderer, which gives zeros

float3 ToLinear(float3 c)
{
    float3 low = c / 12.92;
    float3 high = pow((c + 0.055) / 1.055, 2.4);
    return lerp(high, low, step(c, 0.04045));
}

float3 ToSrgb(float3 l)
{
    float3 low = l * 12.92;
    float3 high = 1.055 * pow(l, 1.0 / 2.4) - 0.055;
    return lerp(high, low, step(l, 0.0031308));
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 one = ddxDdy.x != 0 ? float2(ddxDdy.x, ddxDdy.w) : texel.xy;
    float3 m = floor(tex2D(map, uv).rgb * 255 + 0.5);
    float xLow = floor(m.b / 16);
    float2 move = float2(m.r * 16 + xLow, m.g * 16 + (m.b - xLow * 16)) - 2048;
    float4 c = tex2D(input, uv + move * one);
    float3 colour = c.rgb / max(c.a, 1.0 / 255);
    colour = ToSrgb(floor(ToLinear(colour) * 255 + 0.5) / 255);
    return float4(colour * c.a, c.a);
}
