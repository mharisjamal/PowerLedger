using PowerLedger.App.Aero;
using Shouldly;
using Xunit.Abstractions;

namespace PowerLedger.App.Tests;

/// <summary>The recipe's feTurbulence and displacement, against the reference algorithm and against Chromium's own
/// pixels (ChromiumReference).</summary>
public class TurbulenceTests(ITestOutputHelper output)
{
    [Fact]
    public void Seed_zero_reads_as_one_and_the_generator_is_park_and_miller()
    {
        Turbulence.SetupSeed(0).ShouldBe(1);
        Turbulence.SetupSeed(-5).ShouldBe(6);
        Turbulence.Random(1).ShouldBe(16807);
        Turbulence.Random(16807).ShouldBe(282475249);
        Turbulence.Random(282475249).ShouldBe(1622650073);
    }

    [Fact]
    public void Reference_noise_is_zero_on_the_lattice_and_turbulence_is_its_absolute_sum()
    {
        var noise = new Turbulence(0);
        noise.Noise2(0, 3, 7).ShouldBe(0, 1e-12);
        var one = Math.Abs(noise.Noise2(1, 0.37, 0.81));
        var two = Math.Abs(noise.Noise2(1, 0.74, 1.62)) / 2;
        noise.Sum(1, 37, 81, 0.01, 0.01, 2, fractalSum: false).ShouldBe(one + two, 1e-12);
        noise.Value(1, 37, 81, 0.01, 0.01, 2, fractalSum: true).ShouldBe((noise.Noise2(1, 0.37, 0.81) + noise.Noise2(1, 0.74, 1.62) / 2 + 1) / 2, 1e-12);
    }

    [Fact]
    public void Chromium_gradients_are_the_16_bit_packing_read_back()
    {
        // The packing lengthens every gradient by 256/255 and nudges it by 1/256: noise off the lattice moves a little.
        var reference = new Turbulence(0);
        var chromium = new Turbulence(0, chromiumGradients: true);
        var differs = 0;
        for (var i = 0; i < 100; i++)
        {
            double x = i * 0.137 + 0.05, y = i * 0.071 + 0.3;
            var a = reference.Noise2(0, x, y);
            var b = chromium.Noise2(0, x, y);
            Math.Abs(a - b).ShouldBeLessThan(0.02);
            if (Math.Abs(a - b) > 1e-6) differs++;
        }
        differs.ShouldBeGreaterThan(90);
    }

    [Fact]
    public void Mirror_tiles_like_skia()
    {
        DisplacementField.Mirror(-0.3, 10).ShouldBe(0);
        DisplacementField.Mirror(-1.5, 10).ShouldBe(1);
        DisplacementField.Mirror(9.99, 10).ShouldBe(9);
        DisplacementField.Mirror(10.2, 10).ShouldBe(9);
        DisplacementField.Mirror(11.5, 10).ShouldBe(8);
        DisplacementField.Mirror(25, 10).ShouldBe(5);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("b")]
    [InlineData("c")]
    [InlineData("d")]
    [InlineData("e")]
    public void Every_displaced_pixel_lands_where_chromium_puts_it(string name)
    {
        var scale = ChromiumReference.Cases.Single(c => c.Name == name).Scale;
        var (sx, sy, w, h) = ChromiumReference.Sources(name);
        var field = DisplacementField.Compute(w, h, scale);
        var errors = new List<double>(w * h);
        var exact = 0;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var (px, py) = field.Source(x, y, w, h, LiquidGlassRecipe.Scale);
                int ex = px - sx[y * w + x], ey = py - sy[y * w + x];
                if (ex == 0 && ey == 0) exact++;
                errors.Add(Math.Sqrt(ex * ex + ey * ey));
            }
        }
        errors.Sort();
        var share = (double)exact / (w * h);
        double p99 = errors[(int)(errors.Count * 0.99)], p999 = errors[(int)(errors.Count * 0.999)];
        output.WriteLine($"{name}: {w}x{h} device px at {scale}: {share:P3} on Chromium's pixel, mean {errors.Average():F4} px, 99th {p99:F2} px, 99.9th {p999:F2} px, max {errors[^1]:F1} px");
        share.ShouldBeGreaterThan(0.99);
        p99.ShouldBe(0);
    }
}
