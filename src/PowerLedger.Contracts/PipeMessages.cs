using System.Text.Json.Serialization;

namespace PowerLedger.Contracts;

/// <summary>
/// Everything that travels on the pipe (spec §8): one JSON object per line, its kind in "type". Requests carry an id
/// that their reply repeats; live readings are pushed without one. The type names are the protocol: add kinds, never
/// rename them. A breaking change needs a new pipe name.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SubscribeRequest), "subscribe")]
[JsonDerivedType(typeof(GetStatusRequest), "getStatus")]
[JsonDerivedType(typeof(GetSettingsRequest), "getSettings")]
[JsonDerivedType(typeof(SetSettingsRequest), "setSettings")]
[JsonDerivedType(typeof(SetTariffRequest), "setTariff")]
[JsonDerivedType(typeof(ResetCalibrationRequest), "resetCalibration")]
[JsonDerivedType(typeof(ReportActivityRequest), "reportActivity")]
[JsonDerivedType(typeof(ReportBrightnessRequest), "reportBrightness")]
[JsonDerivedType(typeof(SetConsentRequest), "setConsent")]
[JsonDerivedType(typeof(ReportUsageRequest), "reportUsage")]
[JsonDerivedType(typeof(ReportCrashRequest), "reportCrash")]
[JsonDerivedType(typeof(PreviewUploadRequest), "previewUpload")]
[JsonDerivedType(typeof(SendNowRequest), "sendNow")]
[JsonDerivedType(typeof(DeleteMyDataRequest), "deleteMyData")]
[JsonDerivedType(typeof(SharingReply), "sharing")]
[JsonDerivedType(typeof(OkReply), "ok")]
[JsonDerivedType(typeof(ErrorReply), "error")]
[JsonDerivedType(typeof(StatusReply), "status")]
[JsonDerivedType(typeof(SettingsReply), "settings")]
[JsonDerivedType(typeof(ReadingFrame), "reading")]
public abstract record PipeMessage;

/// <summary>A message from the App that gets exactly one reply carrying the same id.</summary>
public abstract record PipeRequest(long Id) : PipeMessage;

/// <summary>Start receiving a <see cref="ReadingFrame"/> every tick on this connection.</summary>
public sealed record SubscribeRequest(long Id) : PipeRequest(Id);

public sealed record GetStatusRequest(long Id) : PipeRequest(Id);

public sealed record GetSettingsRequest(long Id) : PipeRequest(Id);

/// <summary>Replace the service settings. Refused whole when any value is out of range.</summary>
public sealed record SetSettingsRequest(long Id, ServiceSettings Settings) : PipeRequest(Id);

/// <summary>Add a tariff. <paramref name="EffectiveFrom"/> defaults to now and may be backdated (spec §8).</summary>
public sealed record SetTariffRequest(long Id, decimal PricePerKwh, string Currency, DateTimeOffset? EffectiveFrom) : PipeRequest(Id);

/// <summary>Forget everything learned for this machine and start calibrating afresh.</summary>
public sealed record ResetCalibrationRequest(long Id) : PipeRequest(Id);

/// <summary>Seconds since the last keyboard or mouse input in the sender's session. The service runs in session 0
/// and cannot see input, so the App reports it every few seconds.</summary>
public sealed record ReportActivityRequest(long Id, double IdleSeconds) : PipeRequest(Id);

/// <summary>The brightness and the power state of the external monitors that answered in the sender's session, and the
/// refresh rate and HDR state Windows drives each at, acknowledged with an <see cref="OkReply"/>. The service runs in session
/// 0 and cannot reach the monitors or the user's display settings, so the App reads them: the power state and the display
/// settings every minute, the brightness every few. <paramref name="Power"/> and <paramref name="Displays"/> came later, so a
/// request without them is one from an App that reads neither.</summary>
public sealed record ReportBrightnessRequest(
    long Id,
    IReadOnlyList<MonitorBrightness> Monitors,
    IReadOnlyList<MonitorPowerReading>? Power = null,
    IReadOnlyList<MonitorDisplayReading>? Displays = null)
    : PipeRequest(Id)
{
    /// <summary>Null when every reading is acceptable; otherwise the first problem, in words the App can show.</summary>
    public string? Validate()
    {
        if (Monitors is null) return "The brightness readings are missing.";
        if (Monitors.Count > ServiceSettings.MaxMonitors) return $"At most {ServiceSettings.MaxMonitors} monitors can report a brightness.";
        foreach (var monitor in Monitors)
        {
            if (monitor?.Instance is not { Length: >= 1 and <= 260 }) return "A monitor's instance must be between 1 and 260 characters.";
            if (!double.IsFinite(monitor.Brightness) || monitor.Brightness is < 0 or > 1) return "A brightness must be between 0 and 1.";
        }
        if (Power is not null)
        {
            if (Power.Count > ServiceSettings.MaxMonitors) return $"At most {ServiceSettings.MaxMonitors} monitors can report a power state.";
            foreach (var reading in Power)
            {
                if (reading?.Instance is not { Length: >= 1 and <= 260 }) return "A monitor's instance must be between 1 and 260 characters.";
                if (reading.State is not (MonitorPowerState.On or MonitorPowerState.Standby or MonitorPowerState.Off))
                    return "A monitor's power state must be on, standby or off.";
            }
        }
        if (Displays is not null)
        {
            if (Displays.Count > ServiceSettings.MaxMonitors) return $"At most {ServiceSettings.MaxMonitors} monitors can report how they are driven.";
            foreach (var display in Displays)
            {
                if (display?.Instance is not { Length: >= 1 and <= 260 }) return "A monitor's instance must be between 1 and 260 characters.";
                if (!double.IsFinite(display.RefreshHz) || display.RefreshHz is < 1 or > 1000) return "A refresh rate must be between 1 and 1000 Hz.";
            }
        }
        return null;
    }
}

/// <summary>The request with this id succeeded.</summary>
public sealed record OkReply(long Id) : PipeMessage;

/// <summary>A request failed, or a line could not be read, in which case <paramref name="Id"/> is null.</summary>
public sealed record ErrorReply(long? Id, string Message) : PipeMessage;

public sealed record StatusReply(long Id, ServiceStatus Status) : PipeMessage;

public sealed record SettingsReply(long Id, ServiceSettings Settings) : PipeMessage;

/// <summary>Record the user's answer to the consent dialog, or a change made in Settings → Privacy (data-sharing design
/// §1). Refused when it answers an older wording or shares detailed data without Hardware and power.</summary>
public sealed record SetConsentRequest(long Id, Consent Consent) : PipeRequest(Id);

/// <summary>The App's counts since its last report, added to the day's. Acknowledged, and ignored, while Usage is off.</summary>
public sealed record ReportUsageRequest(long Id, UsageCounts Counts) : PipeRequest(Id);

/// <summary>An App crash caught on an earlier run. Acknowledged, and ignored, while Crash and sensor reports is off.</summary>
public sealed record ReportCrashRequest(long Id, CrashReport Crash) : PipeRequest(Id);

/// <summary>Build what the next upload would carry into a file, and reply with its path in a <see cref="SharingReply"/>.
/// The file, not the pipe, carries it, since a day of minutes is bigger than a pipe message may be.</summary>
public sealed record PreviewUploadRequest(long Id) : PipeRequest(Id);

/// <summary>Send every complete day waiting now, instead of at tonight's minute.</summary>
public sealed record SendNowRequest(long Id) : PipeRequest(Id);

/// <summary>Ask the server to delete everything sent from this PC; on success every switch goes off and the ID is forgotten.</summary>
public sealed record DeleteMyDataRequest(long Id) : PipeRequest(Id);

/// <summary>What a sharing request did, in words the App can show, and for a preview the file it was written to.</summary>
public sealed record SharingReply(long Id, bool Ok, string Message, string? Path = null) : PipeMessage;
