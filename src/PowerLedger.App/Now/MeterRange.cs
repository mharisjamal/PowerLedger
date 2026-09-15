namespace PowerLedger.App;

/// <summary>The meter's full scale (spec §9 MeterScale): the smallest round size above what it must show, with ticks that
/// read cleanly at that size.</summary>
internal readonly record struct MeterRange(double Max, double Major, double Minor)
{
    private static readonly MeterRange[] Sizes =
    [
        new(25, 5, 1), new(50, 10, 2), new(75, 25, 5), new(100, 25, 5), new(150, 25, 5), new(200, 50, 10),
        new(300, 50, 10), new(400, 100, 20), new(600, 100, 20), new(800, 200, 40), new(1000, 250, 50),
        new(1500, 250, 50), new(2000, 500, 100),
    ];

    /// <summary>A scale whose top is at least 10 % above <paramref name="highest"/>, so the needle never pins.</summary>
    public static MeterRange For(double highest)
    {
        var need = double.IsFinite(highest) ? Math.Max(0, highest) * 1.1 : 0;
        foreach (var size in Sizes)
        {
            if (size.Max >= need) return size;
        }
        return Sizes[^1];
    }

    /// <summary>Every tick from 0 to Max; the majors carry labels.</summary>
    public IEnumerable<(double Value, bool Major)> Ticks()
    {
        var count = (int)Math.Round(Max / Minor);
        for (var i = 0; i <= count; i++)
        {
            var value = i * Minor;
            yield return (value, Math.Abs(value / Major - Math.Round(value / Major)) < 1e-9);
        }
    }
}
