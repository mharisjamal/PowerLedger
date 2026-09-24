namespace PowerLedger.Sensors;

/// <summary>
/// The 32-bit build (win-x86, for 32-bit Windows): the vendor libraries NVIDIA, AMD (ADLX) and Intel (Level Zero) ship
/// only for 64-bit Windows, and their drivers no longer support 32-bit Windows at all, so those sources are switched off
/// with this reason instead of being loaded.
/// </summary>
internal static class Bitness
{
    public static string? SixtyFourBitOnly(string library, bool is64BitProcess)
        => is64BitProcess ? null : $"{library} is 64-bit only, and this is the 32-bit build";
}
