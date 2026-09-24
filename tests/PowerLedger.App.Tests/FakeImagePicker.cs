namespace PowerLedger.App.Tests;

/// <summary>Offers whatever paths the test sets, without a real Open dialog.</summary>
internal sealed class FakeImagePicker : IImagePicker
{
    public IReadOnlyList<string> Paths { get; set; } = [];

    public IReadOnlyList<string> Ask() => Paths;
}
