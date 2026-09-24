namespace PowerLedger.App;

/// <summary>Which front end the App shows (Midnight look design §1): Classic, the first one, or Midnight. Chosen in
/// Settings or from either title bar, and kept in ui.json as its name.</summary>
internal enum Look
{
    Classic = 0,
    Midnight = 1,
}
