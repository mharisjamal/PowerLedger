using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;

namespace PowerLedger.App.Aero;

/// <summary>
/// The four ps_2_0 passes of the liquid glass (Shaders/*.fx, compiled to the embedded .ps by
/// scripts/liquid-glass/compile-shaders.ps1). CSS applies a filter list left to right, each function taking the one
/// before's output, so <c>brightness(1.1) blur(2px) url(#displacement)</c> is: brightness, then the blur (across, then
/// down, as Skia's separable blur), then the displacement reading the blurred picture. <see cref="LiquidGlassBackdrop"/>
/// nests one element per pass, the innermost first: WPF applies a child's effect before its parent's, and every pass
/// lands in an 8 bit premultiplied texture, as Chromium's intermediates do. ps_2_0 keeps them within WPF's software
/// renderer too (a render to a bitmap, a remote session), where ps_3_0 draws nothing.
/// </summary>
internal static class LiquidGlassEffects
{
    /// <summary>The blur's largest reach in texels each side that Blur.fx's taps cover: ten bilinear pairs.</summary>
    public const int MaxBlurRadius = 20;

    private static readonly Dictionary<string, PixelShader> Shaders = [];

    /// <summary>
    /// Skia's Gaussian kernel for a deviation of <paramref name="sigma"/> texels (its GPU blur): radius <c>ceil(3 sigma)</c>,
    /// weights <c>exp(-i^2 / (2 sigma^2))</c> normalized over the radius, read as the centre texel and pairs of texels
    /// (1 and 2, 3 and 4, ...) each taken by one bilinear tap at the pair's weighted centre, which returns exactly the two
    /// texels' weighted sum. Radius past <see cref="MaxBlurRadius"/> is held there (a display scale over 3.3 at 2 px).
    /// </summary>
    public static (double Centre, (double Offset, double Weight)[] Pairs) BlurKernel(double sigma)
    {
        if (sigma <= 0) return (1, []);
        var radius = Math.Min(MaxBlurRadius, (int)Math.Ceiling(3 * sigma));
        var weights = new double[radius + 1];
        var total = 0.0;
        for (var i = 0; i <= radius; i++)
        {
            weights[i] = Math.Exp(-(double)i * i / (2 * sigma * sigma));
            total += i == 0 ? weights[i] : 2 * weights[i];
        }
        for (var i = 0; i <= radius; i++) weights[i] /= total;
        var pairs = new List<(double, double)>();
        for (var i = 1; i <= radius; i += 2)
        {
            var second = i + 1 <= radius ? weights[i + 1] : 0;
            var weight = weights[i] + second;
            pairs.Add(((i * weights[i] + (i + 1) * second) / weight, weight));
        }
        return (weights[0], pairs.ToArray());
    }

    internal static PixelShader Shader(string name)
    {
        lock (Shaders)
        {
            if (Shaders.TryGetValue(name, out var shader)) return shader;
            using var stream = typeof(LiquidGlassEffects).Assembly.GetManifestResourceStream($"PowerLedger.App.Aero.LiquidGlass.Shaders.{name}.ps")
                ?? throw new InvalidOperationException($"The {name} shader isn't embedded.");
            shader = new PixelShader();
            shader.SetStreamSource(stream);
            shader.Freeze();
            Shaders[name] = shader;
            return shader;
        }
    }
}

/// <summary>Pass 1: brightness(k).</summary>
internal sealed class GlassBrightnessEffect : ShaderEffect
{
    public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty("Input", typeof(GlassBrightnessEffect), 0);

    public static readonly DependencyProperty BrightnessProperty = DependencyProperty.Register(nameof(Brightness), typeof(double),
        typeof(GlassBrightnessEffect), new UIPropertyMetadata(LiquidGlassRecipe.Brightness, PixelShaderConstantCallback(0)));

    public GlassBrightnessEffect()
    {
        PixelShader = LiquidGlassEffects.Shader("Brightness");
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(BrightnessProperty);
    }

    public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }

    public double Brightness { get => (double)GetValue(BrightnessProperty); set => SetValue(BrightnessProperty, value); }
}

/// <summary>Passes 2 and 3: one direction of the Gaussian blur.</summary>
internal sealed class GlassBlurEffect : ShaderEffect
{
    public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty("Input", typeof(GlassBlurEffect), 0, SamplingMode.Bilinear);

    public static readonly DependencyProperty TexelProperty = DependencyProperty.Register(nameof(Texel), typeof(Point4D), typeof(GlassBlurEffect),
        new UIPropertyMetadata(new Point4D(), PixelShaderConstantCallback(8)));

    public static readonly DependencyProperty AxisProperty = DependencyProperty.Register(nameof(Axis), typeof(Point4D), typeof(GlassBlurEffect),
        new UIPropertyMetadata(new Point4D(1, 0, 0, 0), PixelShaderConstantCallback(1)));

    private static readonly DependencyProperty[] TapProperties = Enumerable.Range(0, 5).Select(i => DependencyProperty.Register($"Taps{i}", typeof(Point4D),
        typeof(GlassBlurEffect), new UIPropertyMetadata(new Point4D(), PixelShaderConstantCallback(2 + i)))).ToArray();

    private static readonly DependencyProperty CentreProperty = DependencyProperty.Register("Centre", typeof(Point4D), typeof(GlassBlurEffect),
        new UIPropertyMetadata(new Point4D(1, 0, 0, 0), PixelShaderConstantCallback(7)));

    private double _sigma = -1;

    public GlassBlurEffect(bool down)
    {
        PixelShader = LiquidGlassEffects.Shader("Blur");
        DdxUvDdyUvRegisterIndex = 0;
        Axis = down ? new Point4D(0, 1, 0, 0) : new Point4D(1, 0, 0, 0);
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(TexelProperty);
        UpdateShaderValue(AxisProperty);   // across is the default value, which a SetValue doesn't send
        UpdateShaderValue(CentreProperty);
        foreach (var tap in TapProperties) UpdateShaderValue(tap);
    }

    public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }

    public Point4D Axis { get => (Point4D)GetValue(AxisProperty); set => SetValue(AxisProperty, value); }

    /// <summary>x and y: one texel of the input in uv, 1 over its width and height in device pixels, for WPF's software
    /// renderer, where DdxUvDdyUv reads zero; the hardware one's own DdxUvDdyUv wins.</summary>
    public Point4D Texel { get => (Point4D)GetValue(TexelProperty); set => SetValue(TexelProperty, value); }

    /// <summary>The deviation in device pixels.</summary>
    public double Sigma
    {
        get => _sigma;
        set
        {
            if (value == _sigma) return;
            _sigma = value;
            var (centre, pairs) = LiquidGlassEffects.BlurKernel(value);
            SetValue(CentreProperty, new Point4D(centre, 0, 0, 0));
            for (var i = 0; i < TapProperties.Length; i++)
            {
                (double Offset, double Weight) Pair(int n) => n < pairs.Length ? pairs[n] : (0, 0);
                var (o1, w1) = Pair(2 * i);
                var (o2, w2) = Pair(2 * i + 1);
                SetValue(TapProperties[i], new Point4D(o1, w1, o2, w2));
            }
        }
    }
}

/// <summary>Pass 4: the displacement, from a source map (DisplacementField.SourceMap).</summary>
internal sealed class GlassDisplaceEffect : ShaderEffect
{
    public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty("Input", typeof(GlassDisplaceEffect), 0, SamplingMode.NearestNeighbor);

    public static readonly DependencyProperty MapProperty = RegisterPixelShaderSamplerProperty(nameof(Map), typeof(GlassDisplaceEffect), 1, SamplingMode.NearestNeighbor);

    public static readonly DependencyProperty TexelProperty = DependencyProperty.Register(nameof(Texel), typeof(Point4D), typeof(GlassDisplaceEffect),
        new UIPropertyMetadata(new Point4D(), PixelShaderConstantCallback(1)));

    public GlassDisplaceEffect()
    {
        PixelShader = LiquidGlassEffects.Shader("Displace");
        DdxUvDdyUvRegisterIndex = 0;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(MapProperty);
        UpdateShaderValue(TexelProperty);
    }

    public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }

    /// <summary>The source map, drawn over the same texels as the input.</summary>
    public Brush Map { get => (Brush)GetValue(MapProperty); set => SetValue(MapProperty, value); }

    /// <summary>x and y: one texel of the input in uv.</summary>
    public Point4D Texel { get => (Point4D)GetValue(TexelProperty); set => SetValue(TexelProperty, value); }
}
