using PowerLedger.Contracts;

namespace PowerLedger.Service;

/// <summary>The latest status and settings the loop has published, for the pipe to read from any thread.</summary>
internal sealed class StatusBoard
{
    private ServiceStatus? _status;
    private ServiceSettings? _settings;

    public ServiceStatus? Status => Volatile.Read(ref _status);

    public ServiceSettings? Settings => Volatile.Read(ref _settings);

    public void Publish(ServiceStatus status) => Volatile.Write(ref _status, status);

    public void Publish(ServiceSettings settings) => Volatile.Write(ref _settings, settings);
}
