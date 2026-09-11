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

/// <summary>The request with this id succeeded.</summary>
public sealed record OkReply(long Id) : PipeMessage;

/// <summary>A request failed, or a line could not be read, in which case <paramref name="Id"/> is null.</summary>
public sealed record ErrorReply(long? Id, string Message) : PipeMessage;

public sealed record StatusReply(long Id, ServiceStatus Status) : PipeMessage;

public sealed record SettingsReply(long Id, ServiceSettings Settings) : PipeMessage;
