using PowerLedger.Storage;

namespace PowerLedger.App;

/// <summary>The Apps ledger's read-only view of the database (Plan AE): what each app used over a range. Local only:
/// nothing read here is ever shared, sent, synced or exported.</summary>
internal interface IAppHistory
{
    /// <summary>The apps over <paramref name="range"/>; null when the database cannot be read, as while the service is
    /// stopped.</summary>
    AppsRange? Apps(DateRange range);
}
