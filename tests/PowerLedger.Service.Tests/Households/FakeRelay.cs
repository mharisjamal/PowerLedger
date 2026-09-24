using System.Buffers.Text;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PowerLedger.Core.Households;

namespace PowerLedger.Service.Tests;

/// <summary>
/// The Worker's household routes in memory, as <c>server/src/households</c> answers them: the signature checks in their
/// order (headers, time, member, signature, replay), membership, batches numbered as they arrive with each item carrying
/// its sender's own sequence number, key envelopes, and meeting slots written once for 10 minutes. N2's routes answer as
/// the Worker's do, with sessions kept as given.
/// </summary>
internal sealed partial class FakeRelay(TimeProvider clock) : HttpMessageHandler
{
    public static readonly Uri Endpoint = new("https://relay.test/");

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Dictionary<string, Member>> _households = [];
    private readonly List<Batch> _batches = [];
    private readonly Dictionary<(string Household, int Epoch, string Device), (string From, string Body)> _envelopes = [];
    private readonly Dictionary<(string Meeting, string Slot), (byte[] Body, DateTimeOffset Created)> _meetings = [];
    private readonly HashSet<string> _seen = [];
    private readonly List<string> _calls = [];

    /// <summary>While true, nothing answers.</summary>
    public bool Down { get; set; }

    /// <summary>Answers a request before the routes do, when it returns one.</summary>
    public Func<HttpRequestMessage, byte[], HttpResponseMessage?>? Intercept { get; set; }

    /// <summary>"METHOD /path" of every request, in order.</summary>
    public List<string> Calls
    {
        get
        {
            lock (_gate) return [.. _calls];
        }
    }

    /// <summary>The bodies of the batches posted, as posted, oldest first.</summary>
    public List<(string Device, int Epoch, long Seq, byte[] Body)> Batches
    {
        get
        {
            lock (_gate) return [.. _batches.Select(batch => (batch.Device, batch.Epoch, batch.DeviceSeq, batch.Body))];
        }
    }

    public int Posted(string what) => Calls.Count(call => call.StartsWith(what, StringComparison.Ordinal));

    /// <summary>The members the server knows, current or not, of a household.</summary>
    public IReadOnlyDictionary<string, Member> Members(string household)
    {
        lock (_gate) return _households.TryGetValue(household, out var members) ? new Dictionary<string, Member>(members) : [];
    }

    /// <summary>Makes a household with its members, as if they had been added one by one.</summary>
    public void Seed(string household, params DeviceKeys[] members)
    {
        lock (_gate)
        {
            var list = _households.TryGetValue(household, out var existing) ? existing : _households[household] = [];
            foreach (var member in members)
            {
                list[member.DeviceId] = new Member(Encode(member.SignPublic), Encode(member.DhPublic), Now, null);
            }
        }
    }

    public void Remove(string household, string device)
    {
        lock (_gate) _households[household][device] = _households[household][device] with { Removed = Now };
    }

    public void PutSlot(string meeting, string slot, byte[] body)
    {
        lock (_gate) _meetings[(meeting, slot)] = (body, clock.GetUtcNow());
    }

    private long Now => clock.GetUtcNow().ToUnixTimeMilliseconds();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
    {
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancel);
        lock (_gate) _calls.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
        if (Down) throw new HttpRequestException("No such host is known.");
        if (Intercept?.Invoke(request, body) is { } intercepted) return intercepted;
        lock (_gate) return Route(request, body);
    }

    private HttpResponseMessage Route(HttpRequestMessage request, byte[] body)
    {
        var path = request.RequestUri!.AbsolutePath;
        var method = request.Method.Method;
        if (method == "POST" && path == "/v1/households") return Create(request, body);
        if (MeetingPath().Match(path) is { Success: true } meeting) return Meeting(method, meeting.Groups[1].Value, meeting.Groups[2].Value, body);
        if (HouseholdPath().Match(path) is not { Success: true } match) return Error(404, "Not found.");

        var household = match.Groups[1].Value;
        var rest = match.Groups[2].Value;
        if (Authenticate(request, body, device => _households.TryGetValue(household, out var members)
                && members.TryGetValue(device, out var member) && member.Removed is null ? member.Sign : null) is { } refused)
        {
            return refused;
        }
        var caller = request.Headers.GetValues("X-PL-Device").Single();
        var list = _households[household];
        switch (method, rest)
        {
            case ("GET", "/members"):
                return Json(new JsonArray([.. list.Select(pair => (JsonNode)new JsonObject
                {
                    ["device"] = pair.Key, ["sign"] = pair.Value.Sign, ["dh"] = pair.Value.Dh, ["added"] = pair.Value.Added,
                    ["removed"] = pair.Value.Removed,
                })]));
            case ("POST", "/members"):
            {
                var posted = JsonNode.Parse(body)!;
                var sign = (string)posted["sign"]!;
                var device = HouseholdCrypto.DeviceIdOf(Decode(sign));
                if (list.TryGetValue(device, out var current) && current.Removed is null) return Ok();
                if (list.Values.Count(member => member.Removed is null) >= 16) return Error(409, "This household already has 16 PCs.");
                list[device] = new Member(sign, (string)posted["dh"]!, Now, null);
                return Ok();
            }
            case ("DELETE", var removing) when removing.StartsWith("/members/", StringComparison.Ordinal):
            {
                var device = removing["/members/".Length..];
                if (!list.TryGetValue(device, out var member) || member.Removed is not null) return Error(404, "That PC isn't a member of this household.");
                list[device] = member with { Removed = Now };
                return Ok();
            }
            case ("POST", "/keys"):
            {
                var posted = JsonNode.Parse(body)!;
                var epoch = (int)posted["epoch"]!;
                var envelopes = posted["envelopes"]!.AsArray().Select(item => ((string)item!["device"]!, (string)item["body"]!)).ToList();
                if (envelopes.Any(item => !list.TryGetValue(item.Item1, out var member) || member.Removed is not null))
                {
                    return Error(400, "Every envelope must be for a current member.");
                }
                var latest = _envelopes.Keys.Where(key => key.Household == household).Select(key => key.Epoch).DefaultIfEmpty(0).Max();
                if (epoch <= latest) return Error(409, $"Epoch {epoch} already has its keys.");
                foreach (var (device, sealedKey) in envelopes) _envelopes[(household, epoch, device)] = (caller, sealedKey);
                return Ok();
            }
            case ("GET", var getting) when getting.StartsWith("/keys/", StringComparison.Ordinal):
            {
                var epoch = int.Parse(getting["/keys/".Length..]);
                return _envelopes.TryGetValue((household, epoch, caller), out var envelope)
                    ? Json(new JsonObject { ["epoch"] = epoch, ["from"] = envelope.From, ["body"] = envelope.Body })
                    : Error(404, "There's no key for this PC at that epoch.");
            }
            case ("POST", "/batches"):
            {
                if (body.Length > 1_048_576) return Error(413, "A batch is at most 1 MB.");
                var posted = JsonNode.Parse(body)!;
                if ((string?)posted["device"] != caller) return Error(400, "device must be this PC's own ID.");
                var seq = _batches.Where(batch => batch.Household == household).Select(batch => batch.Seq).DefaultIfEmpty(0).Max() + 1;
                _batches.Add(new Batch(household, seq, caller, (int)posted["epoch"]!, (long)posted["seq"]!, Decode((string)posted["body"]!)));
                return Ok();
            }
            case ("GET", "/batches"):
            {
                var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
                var after = long.Parse(query["after"] ?? "0");
                var limit = Math.Clamp(int.Parse(query["limit"] ?? "100"), 1, 100);
                var waiting = _batches.Where(batch => batch.Household == household && batch.Seq > after && batch.Device != caller)
                    .OrderBy(batch => batch.Seq).ToList();
                var page = waiting.Take(limit).ToList();
                return Json(new JsonObject
                {
                    ["items"] = new JsonArray([.. page.Select(batch => (JsonNode)new JsonObject
                    {
                        ["seq"] = batch.DeviceSeq, ["device"] = batch.Device, ["epoch"] = batch.Epoch, ["body"] = Encode(batch.Body),
                    })]),
                    ["next"] = page.Count > 0 ? page[^1].Seq : after,
                    ["more"] = waiting.Count > limit,
                });
            }
            default:
                return Error(404, "Not found.");
        }
    }

    private HttpResponseMessage Create(HttpRequestMessage request, byte[] body)
    {
        var posted = JsonNode.Parse(body)!;
        var id = (string)posted["id"]!;
        var sign = (string)posted["sign"]!;
        if (Authenticate(request, body, device => HouseholdCrypto.DeviceIdOf(Decode(sign)) == device ? sign : null) is { } refused) return refused;
        var device = request.Headers.GetValues("X-PL-Device").Single();
        if (_households.TryGetValue(id, out var existing))
        {
            return existing.TryGetValue(device, out var member) && member.Removed is null ? Ok() : Error(409, "A household with this ID already exists.");
        }
        _households[id] = new Dictionary<string, Member> { [device] = new Member(sign, (string)posted["dh"]!, Now, null) };
        return Ok();
    }

    private HttpResponseMessage Meeting(string method, string meeting, string slot, byte[] body)
    {
        var now = clock.GetUtcNow();
        var created = _meetings.Where(pair => pair.Key.Meeting == meeting).Select(pair => (DateTimeOffset?)pair.Value.Created).Min() ?? now;
        var ended = now - created >= TimeSpan.FromMinutes(10);
        if (method == "PUT")
        {
            if (body.Length > 8 * 1024) return Error(413, "A meeting slot holds at most 8 KB.");
            if (ended) return Error(410, "This meeting has ended.");
            if (_meetings.ContainsKey((meeting, slot))) return Error(409, "This slot has already been written.");
            _meetings[(meeting, slot)] = (body, created);
            return Ok();
        }
        if (ended || !_meetings.TryGetValue((meeting, slot), out var kept)) return Error(404, "Nothing is in this slot.");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(kept.Body) };
    }

    /// <summary>The Worker's checks, in its order; null when the request passes.</summary>
    private HttpResponseMessage? Authenticate(HttpRequestMessage request, byte[] body, Func<string, string?> signKeyOf)
    {
        string? Header(string name) => request.Headers.TryGetValues(name, out var values) ? values.Single() : null;
        var device = Header("X-PL-Device");
        var time = Header("X-PL-Time");
        var signature = Header("X-PL-Signature") is { } text ? Decode(text) : null;
        if (device is null || time is null || signature is not { Length: 64 }) return Error(401, "This request needs the X-PL headers.");
        if (Math.Abs(clock.GetUtcNow().ToUnixTimeSeconds() - long.Parse(time)) > 300) return Error(401, "This request's time is more than 5 minutes off.");
        if (signKeyOf(device) is not { } sign) return Error(403, "This PC isn't a member of this household.");
        var signed = HouseholdCrypto.RequestToSign(request.Method.Method, request.RequestUri!.PathAndQuery, long.Parse(time), body);
        if (!HouseholdCrypto.Verify(Decode(sign), signed, signature)) return Error(401, "This request's signature doesn't match.");
        if (!_seen.Add(Convert.ToHexString(signature.AsSpan(0, 32)))) return Error(401, "This request has already been made.");
        return null;
    }

    private static HttpResponseMessage Ok() => Json(new JsonObject { ["ok"] = true });

    private static HttpResponseMessage Json(JsonNode node) =>
        new(HttpStatusCode.OK) { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };

    internal static HttpResponseMessage Error(int status, string message) =>
        new((HttpStatusCode)status) { Content = new StringContent(JsonSerializer.Serialize(new { error = message }), Encoding.UTF8, "application/json") };

    private static string Encode(byte[] bytes) => Base64Url.EncodeToString(bytes);

    private static byte[] Decode(string text) => Base64Url.DecodeFromChars(text);

    [GeneratedRegex("^/v1/households/([0-9a-f]{32})(/.*)$")]
    private static partial Regex HouseholdPath();

    [GeneratedRegex("^/v1/meetings/([0-9a-f]{32})/(adder|joiner|answer|welcome)$")]
    private static partial Regex MeetingPath();

    public sealed record Member(string Sign, string Dh, long Added, long? Removed);

    private sealed record Batch(string Household, long Seq, string Device, int Epoch, long DeviceSeq, byte[] Body);
}
