using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PowerLedger.App;

/// <summary>What the data server says of an Aero request (server/src/aero.ts).</summary>
internal enum AeroRequestState
{
    None,
    Pending,
    Approved,
    Revoked,
}

/// <summary>The data server's Aero request endpoints. Each answers null when the PC is offline or the server failed, so
/// the caller keeps what it last knew.</summary>
internal interface IAeroServer
{
    Task<AeroRequestState?> RequestAsync(string id, string name, CancellationToken cancel = default);

    Task<AeroRequestState?> StatusAsync(string id, CancellationToken cancel = default);
}

/// <summary>POST /v1/aero/request and GET /v1/aero/status on the data server the feedback goes to.</summary>
internal sealed class HttpAeroServer(HttpClient http, Uri? server = null) : IAeroServer
{
    /// <summary>The deployed Worker (server/), as <see cref="FeedbackEndpoint"/> and the service's sharing use.</summary>
    public static Uri BuiltIn { get; } = new("https://powerledger-data.powerledger-data.workers.dev/");

    private readonly Uri _server = server ?? BuiltIn;

    public Task<AeroRequestState?> RequestAsync(string id, string name, CancellationToken cancel = default)
        => SendAsync(token => http.PostAsJsonAsync(new Uri(_server, "v1/aero/request"), new { id, name }, token), cancel);

    public Task<AeroRequestState?> StatusAsync(string id, CancellationToken cancel = default)
        => SendAsync(token => http.GetAsync(new Uri(_server, $"v1/aero/status?id={Uri.EscapeDataString(id)}"), token), cancel);

    /// <summary>The App's shared client waits forever (UpdateHttp), so each call here gives up after this.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static async Task<AeroRequestState?> SendAsync(Func<CancellationToken, Task<HttpResponseMessage>> send, CancellationToken cancel)
    {
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            limit.CancelAfter(Timeout);
            using var response = await send(limit.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            using var answer = JsonDocument.Parse(await response.Content.ReadAsStringAsync(limit.Token).ConfigureAwait(false));
            return answer.RootElement.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String
                ? Parse(state.GetString())
                : null;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    internal static AeroRequestState? Parse(string? state) => state switch
    {
        "none" => AeroRequestState.None,
        "pending" => AeroRequestState.Pending,
        "approved" => AeroRequestState.Approved,
        "revoked" => AeroRequestState.Revoked,
        _ => null,
    };
}

/// <summary>
/// Aero by request (0.10.7, the owner's decision): Request Aero makes this PC's id once, AERO- and 5 Crockford base32
/// characters, keeps it in ui.json and sends it with the PC's name; the user sends the id to the owner, who approves it
/// on the server. The status is checked at start (<see cref="Start"/>), every five minutes while pending, and when
/// Settings shows (<see cref="Check"/>). Approved unlocks Aero, without switching to it, and <see cref="Approved"/> tells
/// the App to say so; revoked locks it again and leaves Aero for Midnight. Offline or a server error keeps what was
/// last known.
/// </summary>
internal sealed class AeroAccess : ObservableObject, IDisposable
{
    public static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(5);

    /// <summary>What a switch to Aero answers while it is locked.</summary>
    public const string Locked = "Aero is by request. Press Request Aero in Settings, Look, and send your request ID to the PowerLedger owner.";

    /// <summary>What the App says once the owner approves.</summary>
    public const string ReadyText = "Aero is ready. Turn it on in Settings, Look.";

    private const string Crockford = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int MaxNameChars = 64;

    private readonly IUiSettings _ui;
    private readonly IAeroServer _server;
    private readonly UiThreads _threads;
    private readonly TimeProvider _clock;
    private readonly Func<string> _pcName;
    private readonly Action<string> _copy;
    private readonly Func<string> _newId;
    private AeroRequestState? _state;
    private ITimer? _timer;

    public AeroAccess(
        IUiSettings ui, IAeroServer server, UiThreads threads, TimeProvider clock, Func<string>? pcName = null,
        Action<string>? copyToClipboard = null, Func<string>? newId = null)
    {
        _ui = ui;
        _server = server;
        _threads = threads;
        _clock = clock;
        _pcName = pcName ?? (() => Environment.MachineName);
        _copy = copyToClipboard ?? (_ => { });
        _newId = newId ?? NewId;
        Request = new RelayCommand(() => _ = RequestAsync());
        CopyId = new RelayCommand(() => { if (RequestId is { } id) _copy(id); });
    }

    /// <summary>The owner approved this PC's request: the App says <see cref="ReadyText"/>.</summary>
    public event Action? Approved;

    /// <summary>The lock changed (approved or revoked), for Settings to show the look and its lock again.</summary>
    public event Action? LockChanged;

    public bool IsLocked => !_ui.Current.AeroApproved;

    /// <summary>The id this PC asked under, or null before Request Aero.</summary>
    public string? RequestId => _ui.Current.AeroRequestId;

    public bool HasRequested => RequestId is not null;

    /// <summary>"Your request ID: AERO-XXXXX. Send it to the PowerLedger owner."; empty before a request.</summary>
    public string RequestText => RequestId is { } id ? $"Your request ID: {id}. Send it to the PowerLedger owner." : "";

    /// <summary>Pending, Approved or Not approved: the server's last answer, or before one, what ui.json keeps.</summary>
    public string Status => (_state ?? (_ui.Current.AeroApproved ? AeroRequestState.Approved : AeroRequestState.Pending)) switch
    {
        AeroRequestState.Approved => "Approved",
        AeroRequestState.Revoked => "Not approved",
        _ => "Pending",
    };

    public ICommand Request { get; }

    public ICommand CopyId { get; }

    /// <summary>Checks now, then every five minutes while the request is pending.</summary>
    public void Start()
    {
        Check();
        _timer ??= _clock.CreateTimer(_ => _threads.Post(() => { if (IsPending) Check(); }), null, CheckEvery, CheckEvery);
    }

    private bool IsPending => HasRequested && IsLocked && _state is not AeroRequestState.Revoked;

    /// <summary>Asks the server where this PC's request stands; nothing before a request. Call on the UI thread.</summary>
    public void Check() => _ = CheckAsync();

    public async Task CheckAsync()
    {
        if (RequestId is not { } id) return;
        var state = await _server.StatusAsync(id).ConfigureAwait(false);
        // The server never heard of it (the request went out offline): asking again is harmless.
        if (state == AeroRequestState.None) state = await _server.RequestAsync(id, Name()).ConfigureAwait(false);
        if (state is { } known) _threads.Post(() => Apply(known));
    }

    /// <summary>Request Aero: the id is made once and kept, then sent with the PC's name. Call on the UI thread.</summary>
    public async Task RequestAsync()
    {
        var id = RequestId;
        if (id is null)
        {
            id = _newId();
            _ui.SetAeroRequestId(id);
            OnPropertyChanged(nameof(RequestId));
            OnPropertyChanged(nameof(HasRequested));
            OnPropertyChanged(nameof(RequestText));
            OnPropertyChanged(nameof(Status));
        }
        var state = await _server.RequestAsync(id, Name()).ConfigureAwait(false);
        if (state is { } known) _threads.Post(() => Apply(known));
    }

    private void Apply(AeroRequestState state)
    {
        _state = state;
        var wasLocked = IsLocked;
        if (state == AeroRequestState.Approved && wasLocked)
        {
            _ui.SetAeroApproved(true);
            Approved?.Invoke();
        }
        else if (state == AeroRequestState.Revoked && !wasLocked)
        {
            _ui.SetAeroApproved(false);
        }
        OnPropertyChanged(nameof(Status));
        if (wasLocked != IsLocked)
        {
            OnPropertyChanged(nameof(IsLocked));
            LockChanged?.Invoke();
        }
    }

    private string Name()
    {
        var name = _pcName().Trim();
        return name.Length == 0 ? "PC" : name.Length > MaxNameChars ? name[..MaxNameChars] : name;
    }

    /// <summary>AERO- and 5 random Crockford base32 characters.</summary>
    internal static string NewId()
        => "AERO-" + string.Concat(Enumerable.Range(0, 5).Select(_ => Crockford[RandomNumberGenerator.GetInt32(Crockford.Length)]));

    /// <summary>Whether <paramref name="id"/> is an id <see cref="NewId"/> could have made.</summary>
    public static bool IsId(string? id)
        => id is { Length: 10 } && id.StartsWith("AERO-", StringComparison.Ordinal) && id[5..].All(Crockford.Contains);

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
