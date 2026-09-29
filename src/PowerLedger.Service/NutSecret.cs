using System.Security.Cryptography;
using System.Text;
using PowerLedger.Contracts;
using PowerLedger.Sensors;
using PowerLedger.Storage;

namespace PowerLedger.Service;

/// <summary>
/// The password for the owner's UPS server (Network UPS Tools), kept as the service's other secrets are: encrypted with
/// Windows' data protection for the service's own account, in the settings table under a key of its own, never in the
/// settings themselves and never sent back to the App. A password arrives inside settings from the App, and is taken out
/// of them before they are stored or published (see <see cref="NutSettings.Password"/>).
/// </summary>
internal sealed class NutSecret(SettingsRepository settings)
{
    internal const string PasswordKey = "nut.password";

    private static readonly byte[] Entropy = "PowerLedger.nut.password.v1"u8.ToArray();

    /// <summary>
    /// The settings without the password: a new one is kept, an empty one forgotten, and none sent keeps what is held. The
    /// password goes too when no UPS server is set up any more. What comes back says whether a password is held.
    /// </summary>
    public ServiceSettings Take(ServiceSettings incoming)
    {
        var nut = incoming.Nut ?? NutSettings.Off;
        if (!nut.IsSetUp || nut.Password is "") settings.Remove(PasswordKey);
        else if (nut.Password is { } password) settings.Set(PasswordKey, Protect(password));
        var held = !string.IsNullOrEmpty(settings.Get(PasswordKey));
        return incoming with { Nut = nut with { Password = null, HasPassword = held } };
    }

    /// <summary>The password held, or null when there is none or it can't be decrypted by this account, as when the database
    /// came from another PC.</summary>
    public string? Password()
    {
        if (settings.Get(PasswordKey) is not { Length: > 0 } kept) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(kept), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception error) when (error is CryptographicException or FormatException)
        {
            return null;
        }
    }

    private static string Protect(string password)
        => Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(password), Entropy, DataProtectionScope.CurrentUser));
}

/// <summary>
/// The owner's UPS server as the sensor thread sees it: the NUT settings the loop last put in force, made into what the
/// NUT source reads. The same target is handed back for as long as the settings are the same object, so the source keeps its
/// connection; any settings applied since are new objects, which starts a new connection, so a new password is always used.
/// Nothing is read once this sensor set has been retired.
/// </summary>
internal sealed class NutSwitch(StatusBoard board, Func<string?> password, CancellationToken retired)
{
    private readonly object _gate = new();
    private NutSettings? _seen;
    private NutTarget? _target;

    public NutTarget? Current()
    {
        if (retired.IsCancellationRequested) return null;
        var nut = board.Settings?.Nut;
        lock (_gate)
        {
            if (ReferenceEquals(nut, _seen)) return _target;
            _seen = nut;
            _target = nut is { IsSetUp: true }
                ? new NutTarget(nut.Host.Trim(), nut.Port, nut.Ups.Trim(),
                    string.IsNullOrWhiteSpace(nut.Username) ? null : nut.Username, nut.HasPassword ? password : null)
                : null;
            return _target;
        }
    }
}
