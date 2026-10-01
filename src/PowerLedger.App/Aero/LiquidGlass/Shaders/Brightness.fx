// Liquid glass pass 1 of 4: CSS brightness(k). Every colour channel times k, clamped (Skia's colour matrix clamps its
// output); the pass's 8 bit target rounds it, as Chromium's intermediate does. Input is premultiplied, so the clamp is
// to alpha. The input is the piece's box with a margin round it for the blur to reach into: a pixel in the margin reads
// the box mirrored at its edge (the edge pixel repeated, Skia's mirror tiling), as Chromium's backdrop blur does.
// Compiled by scripts/liquid-glass/compile-shaders.ps1 to Brightness.ps (ps_2_0).
sampler2D input : register(s0);
float brightness : register(c0);
float4 box : register(c1);        // the box in uv: left, top, right, bottom

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 p = uv;
    p = p < box.xy ? 2 * box.xy - p : p;
    p = p > box.zw ? 2 * box.zw - p : p;
    float4 c = tex2D(input, p);
    c.rgb = min(c.rgb * brightness, c.a);
    return c;
}
