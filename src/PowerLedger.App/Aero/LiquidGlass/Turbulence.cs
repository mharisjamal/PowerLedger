namespace PowerLedger.App.Aero;

/// <summary>
/// feTurbulence, ported from the reference algorithm in SVG 1.1 (15.25) and Filter Effects 1 (feTurbulence), without
/// stitching: the same Park and Miller random numbers, the same lattice and gradients from the seed, the same noise2 and
/// the same sum over octaves (<c>fractalSum</c> adds the signed noise, <c>turbulence</c> its absolute value). A value is
/// the filter's colour in its own colour space, 0 to 1 (linearRGB for the recipe, which leaves color-interpolation-filters
/// at its default): turbulence is <c>sum</c>, fractal noise <c>(sum + 1) / 2</c>, each clamped. Pure and thread safe once
/// made.
/// <para>With <c>chromiumGradients</c>, the gradients are read back as Chromium's GPU path (Skia's GrPerlinNoise2Effect)
/// reads them: each normalized component is packed into 16 bits, <c>round((g + 1) * 32767.5)</c>, stored as two bytes of
/// an 8 bit texture and unpacked as <c>(hi + lo / 256) / 255 * 2 - 1</c>, which is <c>2v / 65280 - 1</c>: every gradient
/// comes out about 0.4 % longer and nudged by 1/256. Measured against Edge (tests, LiquidGlassChromiumTests), the
/// reference gradients put 71 % of displaced pixels on Chromium's pixel and these put 99.8 % there.</para>
/// </summary>
internal sealed class Turbulence
{
    private const int BSize = 0x100;
    private const int BM = 0xff;
    private const int PerlinN = 0x1000;
    private const int RandM = 2147483647;   // 2^31 - 1
    private const int RandA = 16807;        // 7^5, a primitive root of m
    private const int RandQ = 127773;       // m / a
    private const int RandR = 2836;         // m % a

    private readonly int[] _lattice = new int[BSize + BSize + 2];
    private readonly double[,,] _gradient = new double[4, BSize + BSize + 2, 2];

    public Turbulence(int seed, bool chromiumGradients = false)
    {
        var s = SetupSeed(seed);
        int i = 0, j, k;
        for (k = 0; k < 4; k++)
        {
            for (i = 0; i < BSize; i++)
            {
                _lattice[i] = i;
                for (j = 0; j < 2; j++)
                {
                    s = Random(s);
                    _gradient[k, i, j] = (double)((s % (BSize + BSize)) - BSize) / BSize;
                }
                var length = Math.Sqrt(_gradient[k, i, 0] * _gradient[k, i, 0] + _gradient[k, i, 1] * _gradient[k, i, 1]);
                // The reference divides by the length unguarded; a zero gradient (both draws exactly BSize) stays zero,
                // as Skia's normalize leaves it.
                if (length > 0)
                {
                    _gradient[k, i, 0] /= length;
                    _gradient[k, i, 1] /= length;
                }
            }
        }
        while (--i > 0)
        {
            k = _lattice[i];
            s = Random(s);
            j = s % BSize;
            _lattice[i] = _lattice[j];
            _lattice[j] = k;
        }
        if (chromiumGradients)
        {
            for (k = 0; k < 4; k++)
            {
                for (var n = 0; n < BSize; n++)
                {
                    for (j = 0; j < 2; j++) _gradient[k, n, j] = 2 * Math.Floor((_gradient[k, n, j] + 1) * 32767.5 + 0.5) / 65280 - 1;
                }
            }
        }
        for (i = 0; i < BSize + 2; i++)
        {
            _lattice[BSize + i] = _lattice[i];
            for (k = 0; k < 4; k++)
            {
                for (j = 0; j < 2; j++) _gradient[k, BSize + i, j] = _gradient[k, i, j];
            }
        }
    }

    /// <summary>The reference's setup_seed: a seed at or under 0 becomes <c>-(seed % (m - 1)) + 1</c> (0 reads as 1).</summary>
    internal static int SetupSeed(long seed)
    {
        if (seed <= 0) seed = -(seed % (RandM - 1)) + 1;
        if (seed > RandM - 1) seed = RandM - 1;
        return (int)seed;
    }

    /// <summary>The reference's random: Park and Miller's minimal standard generator, by Schrage's method.</summary>
    internal static int Random(int seed)
    {
        var result = RandA * (seed % RandQ) - RandR * (seed / RandQ);
        if (result <= 0) result += RandM;
        return result;
    }

    /// <summary>The reference's noise2 for one channel (0 red to 3 alpha) at <paramref name="x"/>, <paramref name="y"/> in
    /// noise space (already times the base frequency).</summary>
    public double Noise2(int channel, double x, double y)
    {
        var t = x + PerlinN;
        var bx0 = (int)t & BM;
        var bx1 = (bx0 + 1) & BM;
        var rx0 = t - (int)t;
        var rx1 = rx0 - 1.0;
        t = y + PerlinN;
        var by0 = (int)t & BM;
        var by1 = (by0 + 1) & BM;
        var ry0 = t - (int)t;
        var ry1 = ry0 - 1.0;
        var i = _lattice[bx0];
        var j = _lattice[bx1];
        var b00 = _lattice[i + by0];
        var b10 = _lattice[j + by0];
        var b01 = _lattice[i + by1];
        var b11 = _lattice[j + by1];
        var sx = rx0 * rx0 * (3.0 - 2.0 * rx0);
        var sy = ry0 * ry0 * (3.0 - 2.0 * ry0);
        var u = rx0 * _gradient[channel, b00, 0] + ry0 * _gradient[channel, b00, 1];
        var v = rx1 * _gradient[channel, b10, 0] + ry0 * _gradient[channel, b10, 1];
        var a = u + sx * (v - u);
        u = rx0 * _gradient[channel, b01, 0] + ry1 * _gradient[channel, b01, 1];
        v = rx1 * _gradient[channel, b11, 0] + ry1 * _gradient[channel, b11, 1];
        var b = u + sx * (v - u);
        return a + sy * (b - a);
    }

    /// <summary>The reference's turbulence() sum for one channel at a point in the filter's user space.</summary>
    public double Sum(int channel, double x, double y, double baseFrequencyX, double baseFrequencyY, int octaves, bool fractalSum)
    {
        var sum = 0.0;
        var vx = x * baseFrequencyX;
        var vy = y * baseFrequencyY;
        var ratio = 1.0;
        for (var octave = 0; octave < octaves; octave++)
        {
            var noise = Noise2(channel, vx, vy);
            sum += (fractalSum ? noise : Math.Abs(noise)) / ratio;
            vx *= 2;
            vy *= 2;
            ratio *= 2;
        }
        return sum;
    }

    /// <summary>One channel's colour, 0 to 1, as the filter outputs it (before premultiplying).</summary>
    public double Value(int channel, double x, double y, double baseFrequencyX, double baseFrequencyY, int octaves, bool fractalSum)
    {
        var sum = Sum(channel, x, y, baseFrequencyX, baseFrequencyY, octaves, fractalSum);
        return Math.Clamp(fractalSum ? (sum + 1) / 2 : sum, 0, 1);
    }
}
