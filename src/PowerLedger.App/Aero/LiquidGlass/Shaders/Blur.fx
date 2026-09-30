// Liquid glass passes 2 and 3 of 4: one direction of CSS blur(), a Gaussian of deviation sigma device pixels over
// ceil(3 sigma) pixels each side, as Skia's separable GPU blur (its linear kernel: pairs of texels read through one
// bilinear tap at the pair's weighted centre). The weights and offsets come from LiquidGlassEffects.BlurKernel. Pixels
// past the piece's box are the box mirrored (the element's brush tiles FlipXY), as Chromium's backdrop blur mirrors.
// Compiled by scripts/liquid-glass/compile-shaders.ps1 to Blur.ps (ps_2_0).
sampler2D input : register(s0);
float4 ddxDdy : register(c0);     // WPF's DdxUvDdyUv: one texel across (xy) and down (zw), where the renderer gives it
float4 axis : register(c1);       // (1, 0) across, (0, 1) down
float4 taps[5] : register(c2);    // (offset, weight, offset, weight): the pairs' centres in pixels, one side
float4 centre : register(c7);     // x: the centre texel's weight
float4 texel : register(c8);      // xy: one texel from the input's size, for the software renderer, which gives zeros

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 one = ddxDdy.x != 0 ? float2(ddxDdy.x, ddxDdy.w) : texel.xy;
    float2 step = axis.xy * one;
    float4 sum = tex2D(input, uv) * centre.x;
    [unroll] for (int i = 0; i < 5; i++)
    {
        sum += (tex2D(input, uv + step * taps[i].x) + tex2D(input, uv - step * taps[i].x)) * taps[i].y;
        sum += (tex2D(input, uv + step * taps[i].z) + tex2D(input, uv - step * taps[i].z)) * taps[i].w;
    }
    return sum;
}
