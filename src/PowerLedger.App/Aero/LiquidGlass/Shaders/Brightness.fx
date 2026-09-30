// Liquid glass pass 1 of 4: CSS brightness(k). Every colour channel times k, clamped (Skia's colour matrix clamps its
// output); the pass's 8 bit target rounds it, as Chromium's intermediate does. Input is premultiplied, so the clamp is
// to alpha. Compiled by scripts/liquid-glass/compile-shaders.ps1 to Brightness.ps (ps_2_0).
sampler2D input : register(s0);
float brightness : register(c0);

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float4 c = tex2D(input, uv);
    c.rgb = min(c.rgb * brightness, c.a);
    return c;
}
