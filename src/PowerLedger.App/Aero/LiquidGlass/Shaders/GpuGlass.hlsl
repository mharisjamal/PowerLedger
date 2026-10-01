// The liquid glass on the GPU path (GpuGlass.cs): Direct3D 11 shaders over the duplicated desktop, the recipe as
// Chromium runs it on the GPU, each step landing in an 8 bit texture where Chromium's intermediates are 8 bit:
//   Bright   brightness(k) on each texel of the box and a margin round it (clamped); a margin texel reads the box
//            mirrored (Skia's mirror tiling, the edge texel repeated), as Chromium's backdrop blur does.
//   Across   the Gaussian across, as Skia's GPU blur reads it: the centre texel, then pairs of texels each read through
//            one bilinear tap at the pair's weighted centre (its linear kernel).
// Bright and Across leave the rounding to the 8 bit render target, as Chromium's passes do (a value half way between two
// steps, as brightness(1.1) makes of every tenth input, rounds as the hardware rounds it, not as floor(x + 0.5) would).
//   Down     the Gaussian down, the same way, at the texel the source map names (DisplacementField: the move and the
//            mirror as Chromium makes them), rounded, then through 8 bit linearRGB and back (feDisplacementMap works in
//            linearRGB).
//   Compare  whether any texel of a rectangle differs between two textures: a frame whose change is only our own window
//            repainting (left out of capture, so the pixels are the same) is dropped without drawing.
// Compiled by scripts/liquid-glass/compile-shaders.ps1 to GpuGlass.<entry>.cso (vs_4_0, ps_4_0, cs_5_0).

Texture2D<float4> Desktop : register(t0);   // Bright: the desktop (8 bit sRGB, or scRGB half floats on an HDR monitor)
Texture2D<float4> Source : register(t1);    // Across: Bright's output; Down: Across's output
Texture2D<uint2> Map : register(t2);        // Down: each texel's source in the box
SamplerState Linear : register(s0);         // bilinear, clamped

cbuffer Glass : register(b0)
{
    int2 Box;          // the box's top left on the desktop texture
    int2 Size;         // the box's width and height
    int2 Out;          // Down: where the piece's rectangle starts on the render target
    int Margin;        // texels of margin round the box
    int Pairs;         // how many bilinear pairs each side
    int2 Limit;        // the desktop texture's size
    float Brightness;  // brightness(k)
    float SdrWhite;    // scRGB value of SDR white on an HDR desktop; 0 for an 8 bit one
    float2 SourceSize; // the size of the texture Source is, in texels
    float Centre;      // the centre texel's weight
    float Pad;
    float4 Radii;      // Down: the piece's corners in pixels (top left, top right, bottom right, bottom left), drawn
                       // antialiased into alpha where the piece itself is the clip (DirectComposition); zero for none
    float4 Taps[16];   // (offset, weight, offset, weight): the pairs' centres in texels, one side
};

float4 Fullscreen(uint id : SV_VertexID) : SV_Position
{
    float2 uv = float2((id << 1) & 2, id & 2);
    return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}

float3 Round8(float3 c)
{
    return floor(c * 255 + 0.5) / 255;
}

// Skia's mirror tiling for whole texels: ..., 2, 1, 0 | 0, 1, 2, ..., n - 1 | n - 1, n - 2, ...
int Mirror(int i, int n)
{
    int period = 2 * n;
    i = ((i % period) + period) % period;
    return i < n ? i : period - 1 - i;
}

float3 ToLinear(float3 c)
{
    return c <= 0.04045 ? c / 12.92 : pow(abs((c + 0.055) / 1.055), 2.4);
}

float3 ToSrgb(float3 l)
{
    return l <= 0.0031308 ? l * 12.92 : 1.055 * pow(abs(l), 1 / 2.4) - 0.055;
}

// A box texel as the browser would have it: 8 bit sRGB. An HDR desktop's scRGB is brought to SDR white, clamped,
// encoded and rounded, as Windows shows SDR content there.
float3 Fetch(int2 q)
{
    float3 c = Desktop.Load(int3(clamp(Box + q, int2(0, 0), Limit - 1), 0)).rgb;
    if (SdrWhite > 0) c = Round8(ToSrgb(saturate(c / SdrWhite)));
    return c;
}

float4 Bright(float4 position : SV_Position) : SV_Target
{
    int2 p = int2(position.xy) - Margin;
    return float4(min(Fetch(int2(Mirror(p.x, Size.x), Mirror(p.y, Size.y))) * Brightness, 1), 1);
}

// The Gaussian along axis at texel centre t (in texels) of Source, unrounded.
float3 Blur(float2 t, float2 axis)
{
    float3 sum = Source.SampleLevel(Linear, t / SourceSize, 0).rgb * Centre;
    for (int i = 0; i < Pairs; i++)
    {
        float4 tap = Taps[i >> 1];
        float2 ow = (i & 1) == 0 ? tap.xy : tap.zw;
        sum += (Source.SampleLevel(Linear, (t + axis * ow.x) / SourceSize, 0).rgb + Source.SampleLevel(Linear, (t - axis * ow.x) / SourceSize, 0).rgb) * ow.y;
    }
    return sum;
}

float4 Across(float4 position : SV_Position) : SV_Target
{
    int2 p = int2(position.xy);
    return float4(Blur(float2(p.x + Margin, p.y) + 0.5, float2(1, 0)), 1);
}

// How much of the pixel at centre q lies inside the box's rounded corners: 1 away from them, falling to 0 over a pixel
// across each corner's arc.
float Cover(float2 q)
{
    float2 size = float2(Size);
    float r = q.x < size.x / 2 ? (q.y < size.y / 2 ? Radii.x : Radii.w) : (q.y < size.y / 2 ? Radii.y : Radii.z);
    if (r <= 0) return 1;
    float2 corner = float2(q.x < size.x / 2 ? r : size.x - r, q.y < size.y / 2 ? r : size.y - r);
    float2 d = (q - corner) * float2(q.x < size.x / 2 ? -1 : 1, q.y < size.y / 2 ? -1 : 1);
    if (d.x <= 0 || d.y <= 0) return 1;
    return saturate(r - length(d) + 0.5);
}

float4 Down(float4 position : SV_Position) : SV_Target
{
    int2 p = int2(position.xy) - Out;
    int2 s = int2(Map.Load(int3(p, 0)));
    float3 c = Round8(Blur(float2(s.x, s.y + Margin) + 0.5, float2(0, 1)));
    float a = Cover(float2(p) + 0.5);
    return float4(ToSrgb(Round8(ToLinear(c))) * a, a);
}

Texture2D<float4> Now : register(t0);
Texture2D<float4> Before : register(t1);
RWByteAddressBuffer Differs : register(u0);

cbuffer Area : register(b1)
{
    int2 Origin;   // the rectangle's top left on both textures
    int2 Extent;   // its width and height
    int Slot;      // which word of Differs to set: one per window
};

[numthreads(16, 16, 1)]
void Compare(uint3 id : SV_DispatchThreadID)
{
    if (any(int2(id.xy) >= Extent)) return;
    int3 q = int3(Origin + int2(id.xy), 0);
    if (any(Now.Load(q) != Before.Load(q))) Differs.Store(Slot * 4, 1);
}
