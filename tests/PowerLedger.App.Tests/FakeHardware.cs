namespace PowerLedger.App.Tests;

/// <summary>A PC's part models without WMI.</summary>
internal sealed class FakeHardware : IHardwareNames
{
    public IReadOnlyDictionary<Part, string> Read() => new Dictionary<Part, string>
    {
        [Part.Cpu] = "Intel Core i7-8650U",
        [Part.Gpu] = "NVIDIA GeForce RTX 3060",
        [Part.Display] = "DELL U2720Q",
        [Part.Rest] = "16 GB DDR4 RAM, Samsung SSD 980 1TB",
    };
}
