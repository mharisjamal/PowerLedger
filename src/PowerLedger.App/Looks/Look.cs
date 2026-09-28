namespace PowerLedger.App;

/// <summary>Which front end the App shows (Midnight look design §1, Aero look design §1): Classic, the first one;
/// Midnight; or Aero, the Liquid Glass look and the default since 0.10.0. Chosen in Settings or from any title bar, and
/// kept in ui.json as its name, so the numbers only ever grow.</summary>
internal enum Look
{
    Classic = 0,
    Midnight = 1,
    Aero = 2,
}
