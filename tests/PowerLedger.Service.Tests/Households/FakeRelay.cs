using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PowerLedger.Core.Households;

namespace PowerLedger.Service.Tests;

/// <summary>
/// The Worker's household routes in memory, as <c>server/src/households</c> answers them after the security re-review: the
/// signature checks in their order (headers, time, member, signature, replay), 401 for a PC that isn't a member and 410 for
/// one that was removed; a member added only with the joiner's own proof; members with the household's epoch when each
/// was added and removed (plan 0.9); the household's current epoch, which a rotation must follow by one and an approval
/// must use; a household kept as ended when its last member goes; batches numbered from a counter that never goes back,
/// each item carrying its sender's own sequence number and signature, kept as sent; meeting slots written once for 10
/// minutes; and N2's accounts, sessions, links, requests and recovery.
/// </summary>
internal sealed partial class FakeRelay(TimeProvider clock) : HttpMessageHandler
{
    public static readonly Uri Endpoint = new("https://relay.test/");

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Dictionary<string, Member>> _households = [];
    private readonly Dictionary<string, int> _epochs = [];
    private readonly Dictionary<string, long> _nextSeq = [];
    private readonly List<Batch> _batches = [];
    private readonly Dictionary<(string Household, int Epoch, string Device), (string From, string Body)> _envelopes = [];
    private readonly Dictionary<(string Meeting, string Slot), (byte[] Body, DateTimeOffset Created)> _meetings = [];
    private readonly HashSet<string> _seen = [];
    private readonly List<string> _calls = [];
    private readonly List<(string Call, byte[] Body)> _sent = [];
    private readonly Dictionary<(string Provider, string Subject), string> _accounts = [];
    private readonly Dictionary<string, Session> _sessions = [];
    private readonly Dictionary<string, string> _links = [];
    private readonly Dictionary<(string Household, string Device), JoinRequest> _requests = [];
    private readonly Dictionary<string, Recovery> _recovery = [];
    private readonly Dictionary<(string Account, string Verifier), (string Device, string Household, int Epoch, long Used)> _recovered = [];

    /// <summary>While true, nothing answers.</summary>
    public bool Down { get; set; }

    /// <summary>Answers a request before the routes do, when it returns one.</summary>
    public Func<HttpRequestMessage, byte[], HttpResponseMessage?>? Intercept { get; set; }

    /// <summary>Requests the server carries out but whose answer is lost on the way back, when it says so.</summary>
    public Func<HttpRequestMessage, bool>? LoseAnswer { get; set; }

    /// <summary>The bodies of every request as sent, with "METHOD /path", in order.</summary>
    public List<(string Call, byte[] Body)> Sent
    {
        get
        {
            lock (_gate) return [.. _sent];
        }
    }

    /// <summary>"METHOD /path" of every request, in order.</summary>
    public List<string> Calls
    {
        get
        {
            lock (_gate) return [.. _calls];
        }
    }

    /// <summary>The bodies of the batches posted, as posted, with their signatures, oldest first.</summary>
    public List<(string Device, int Epoch, long Seq, byte[] Body, byte[] Sig)> Batches
    {
        get
        {
            lock (_gate) return [.. _batches.Select(batch => (batch.Device, batch.Epoch, batch.DeviceSeq, batch.Body, Decode(batch.Sig)))];
        }
    }

    public int Posted(string what) => Calls.Count(call => call.StartsWith(what, StringComparison.Ordinal));

    /// <summary>The members the server knows, current or not, of a household.</summary>
    public IReadOnlyDictionary<string, Member> Members(string household)
    {
        lock (_gate) return _households.TryGetValue(household, out var members) ? new Dictionary<string, Member>(members) : [];
    }

    /// <summary>The PCs the key of an epoch was sealed to.</summary>
    public IReadOnlyList<string> Sealed(string household, int epoch)
    {
        lock (_gate) return [.. _envelopes.Keys.Where(key => key.Household == household && key.Epoch == epoch).Select(key => key.Device)];
    }

    /// <summary>The household's current epoch as the server tracks it.</summary>
    public int Epoch(string household)
    {
        lock (_gate) return _epochs.GetValueOrDefault(household, 1);
    }

    /// <summary>Makes a household with its members, as if they had been added one by one at its current epoch.</summary>
    public void Seed(string household, params DeviceKeys[] members)
    {
        lock (_gate)
        {
            var list = _households.TryGetValue(household, out var existing) ? existing : _households[household] = [];
            _epochs.TryAdd(household, 1);
            foreach (var member in members)
            {
                list[member.DeviceId] = new Member(Encode(member.SignPublic), Encode(member.DhPublic), Now, null, _epochs[household]);
            }
        }
    }

    /// <summary>Removes a member as another member's request would.</summary>
    public void Remove(string household, string device)
    {
        lock (_gate) RemoveMember(household, device);
    }

    /// <summary>Drops the household's batches, or those numbered up to <paramref name="throughSeq"/>, as retention does after
    /// 90 days; the numbering goes on from where it was.</summary>
    public void DropBatches(string household, long throughSeq = long.MaxValue)
    {
        lock (_gate) _batches.RemoveAll(batch => batch.Household == household && batch.Seq <= throughSeq);
    }

    /// <summary>The number the household's newest batch was given.</summary>
    public long LastSeq(string household)
    {
        lock (_gate) return _nextSeq.GetValueOrDefault(household);
    }

    /// <summary>True once the household's last member has gone.</summary>
    public bool Ended(string household)
    {
        lock (_gate) return _households.TryGetValue(household, out var members) && members.Values.All(member => member.Removed is not null);
    }

    public void PutSlot(string meeting, string slot, byte[] body)
    {
        lock (_gate) _meetings[(meeting, slot)] = (body, clock.GetUtcNow());
    }

    /// <summary>What a meeting slot holds, as the server sees it; null while it is empty.</summary>
    public byte[]? Slot(string meeting, string slot)
    {
        lock (_gate) return _meetings.TryGetValue((meeting, slot), out var kept) ? kept.Body : null;
    }

    /// <summary>N2: an ID token as the fake checks it: the provider, the subject and the nonce it was made for.</summary>
    public static string IdToken(string provider, string subject, string deviceId, string salt) => $"{provider}|{subject}|{BoundNonce(deviceId, salt)}";

    /// <summary>N2: the nonce the App asks the provider for, as the Worker works it out (signin.ts boundNonce).</summary>
    public static string BoundNonce(string deviceId, string salt) => Encode(SHA256.HashData(Encoding.UTF8.GetBytes($"{deviceId}:{salt}")));

    /// <summary>N2: the household the account of <paramref name="subject"/> is linked to.</summary>
    public string? LinkOf(string subject)
    {
        lock (_gate) return AccountOf(subject) is { } account ? _links.GetValueOrDefault(account) : null;
    }

    /// <summary>N2: takes the account's link away, as the Worker does when the PC that linked it is removed.</summary>
    public void Unlink(string subject)
    {
        lock (_gate)
        {
            if (AccountOf(subject) is { } account) _links.Remove(account);
        }
    }

    /// <summary>N2: the recovery envelope of the account of <paramref name="subject"/>.</summary>
    public Recovery? RecoveryOf(string subject)
    {
        lock (_gate) return AccountOf(subject) is { } account && _recovery.TryGetValue(account, out var envelope) ? envelope : null;
    }

    /// <summary>N2: the devices waiting to join a household.</summary>
    public IReadOnlyList<string> Waiting(string household)
    {
        lock (_gate) return [.. _requests.Where(pair => pair.Key.Household == household && Waiting(pair.Key, pair.Value)).Select(pair => pair.Key.Device)];
    }

    /// <summary>N2: links an account, by its opaque ID, to a household, as a PC of it signed in as that account would.</summary>
    public void Link(string account, string household)
    {
        lock (_gate) _links[account] = household;
    }

    /// <summary>N2: the waiting PC's nonce, as that PC would send it once a member has committed.</summary>
    public void Answer(string household, string device, byte[] nonce)
    {
        lock (_gate) _requests[(household, device)] = _requests[(household, device)] with { Nonce = Encode(nonce) };
    }

    /// <summary>N2: the server changes what it keeps, and so lists, of a waiting PC's request.</summary>
    public void Rewrite(string household, string device, Func<JoinRequest, JoinRequest> change)
    {
        lock (_gate) _requests[(household, device)] = change(_requests[(household, device)]);
    }

    /// <summary>N2: another member turns a waiting PC away.</summary>
    public void Deny(string household, string device)
    {
        lock (_gate) _requests.Remove((household, device));
    }

    /// <summary>N2: a PC's request to join, as the server keeps it; null when there is none.</summary>
    public JoinRequest? RequestOf(string household, string device)
    {
        lock (_gate) return _requests.GetValueOrDefault((household, device));
    }

    /// <summary>N2: a PC signed in as another account asks to join the household that account is linked to.</summary>
    public void Ask(string household, DeviceKeys pc, string account)
    {
        lock (_gate) _requests[(household, pc.DeviceId)] = new JoinRequest(account, Encode(pc.SignPublic), Encode(pc.DhPublic), Now);
    }

    public int Sessions
    {
        get
        {
            lock (_gate) return _sessions.Count;
        }
    }

    private long Now => clock.GetUtcNow().ToUnixTimeMilliseconds();

    private string? AccountOf(string subject) => _accounts.Where(pair => pair.Key.Subject == subject).Select(pair => pair.Value).FirstOrDefault();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
    {
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancel);
        lock (_gate)
        {
            _calls.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            _sent.Add(($"{request.Method} {request.RequestUri!.AbsolutePath}", body));
        }
        if (Down) throw new HttpRequestException("No such host is known.");
        HttpResponseMessage response;
        if (Intercept?.Invoke(request, body) is { } intercepted) response = intercepted;
        else lock (_gate) response = Route(request, body);
        if (LoseAnswer?.Invoke(request) == true) throw new HttpRequestException("The connection was reset.");
        response.Headers.Date = clock.GetUtcNow();                             // the server's own clock, as the Worker's answers carry it
        return response;
    }

    private HttpResponseMessage Route(HttpRequestMessage request, byte[] body)
    {
        var path = request.RequestUri!.AbsolutePath;
        var method = request.Method.Method;
        if (method == "POST" && path == "/v1/households") return Create(request, body);
        if (method == "POST" && path == "/v1/auth/signin") return SignIn(request, body);
        if (path.StartsWith("/v1/account", StringComparison.Ordinal) || path == "/v1/auth/signout") return Account(request, method, path, body);
        if (MeetingPath().Match(path) is { Success: true } meeting) return Meeting(method, meeting.Groups[1].Value, meeting.Groups[2].Value, body);
        if (HouseholdPath().Match(path) is not { Success: true } match) return Error(404, "Not found.");

        var household = match.Groups[1].Value;
        var rest = match.Groups[2].Value;
        if (Authenticate(request, body, device => _households.TryGetValue(household, out var members) && members.TryGetValue(device, out var member)
                ? (member.Sign, member.Removed is not null)
                : null) is { } refused)
        {
            return refused;
        }
        var caller = request.Headers.GetValues("X-PL-Device").Single();
        var list = _households[household];
        switch (method, rest)
        {
            case ("GET", "/members"):
                return Json(new JsonObject
                {
                    ["members"] = new JsonArray([.. list.Select(pair => (JsonNode)new JsonObject
                    {
                        ["device"] = pair.Key, ["sign"] = pair.Value.Sign, ["dh"] = pair.Value.Dh, ["added"] = pair.Value.Added,
                        ["removed"] = pair.Value.Removed, ["addedEpoch"] = pair.Value.AddedEpoch, ["removedEpoch"] = pair.Value.RemovedEpoch,
                    })]),
                });
            case ("POST", "/members"):
            {
                var posted = JsonNode.Parse(body)!;
                var sign = (string)posted["sign"]!;
                var dh = (string)posted["dh"]!;
                var proof = posted["proof"] is { } given ? Decode((string)given!) : [];
                if (!HouseholdCrypto.Verify(Decode(sign), Encoding.UTF8.GetBytes($"powerledger join|{household}|{sign}|{dh}"), proof))
                {
                    return Error(400, "proof must be the joining PC's signature over its join.");
                }
                var device = HouseholdCrypto.DeviceIdOf(Decode(sign));
                if (list.TryGetValue(device, out var current) && current.Removed is null) return current.Dh == dh ? Ok() : Error(409, "Another key-agreement key.");
                if (list.Values.Count(member => member.Removed is null) >= 16) return Error(409, "This household already has 16 PCs.");
                list[device] = new Member(sign, dh, Now, null, _epochs.GetValueOrDefault(household, 1));   // added back: its removal cleared
                _requests.Remove((household, device));
                return Ok();
            }
            case ("DELETE", var removing) when removing.StartsWith("/members/", StringComparison.Ordinal):
            {
                var device = removing["/members/".Length..];
                if (!list.ContainsKey(device)) return Error(404, "That PC isn't a member of this household.");
                RemoveMember(household, device);                                  // one already removed is done
                return Ok();
            }
            case ("POST", "/keys"):
            {
                var posted = JsonNode.Parse(body)!;
                var epoch = (int)posted["epoch"]!;
                var envelopes = posted["envelopes"]!.AsArray().Select(item => ((string)item!["device"]!, (string)item["body"]!)).ToList();
                if (envelopes.Count is 0 or > 16) return Error(400, "envelopes must be 1 to 16 of {\"device\",\"body\"}, one per PC.");
                var current = _epochs.GetValueOrDefault(household, 1);
                var mine = _envelopes.Where(pair => pair.Key.Household == household && pair.Key.Epoch == epoch && pair.Value.From == caller).ToList();
                if (epoch == current && mine.Count == envelopes.Count
                    && envelopes.All(item => mine.Any(pair => pair.Key.Device == item.Item1 && pair.Value.Body == item.Item2)))
                {
                    return Ok();                                                   // this PC's very rotation again, whatever changed since
                }
                if (envelopes.Any(item => !list.TryGetValue(item.Item1, out var member) || member.Removed is not null))
                {
                    return Error(400, "Every envelope must be for a current member.");
                }
                if (envelopes.Count != list.Values.Count(member => member.Removed is null))
                {
                    return Error(409, "Every current member needs the new key; look at the members again.", current);
                }
                if (epoch != current + 1) return Error(409, $"The household's key is at epoch {current}; new keys are for epoch {current + 1} only.", current);
                foreach (var (device, sealedKey) in envelopes) _envelopes[(household, epoch, device)] = (caller, sealedKey);
                _epochs[household] = epoch;
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
                if (posted["sig"] is not { } sig || Decode((string)sig!).Length != 64)
                {
                    return Error(400, "sig must be the sender's signature over the batch: 64 bytes, as base64url.");
                }
                var seq = _nextSeq[household] = _nextSeq.GetValueOrDefault(household) + 1;
                _batches.Add(new Batch(household, seq, caller, (int)posted["epoch"]!, (long)posted["seq"]!, Decode((string)posted["body"]!), (string)sig!));
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
                        ["seq"] = batch.DeviceSeq, ["device"] = batch.Device, ["epoch"] = batch.Epoch, ["body"] = Encode(batch.Body), ["sig"] = batch.Sig,
                    })]),
                    ["next"] = page.Count > 0 ? page[^1].Seq : after,
                    ["more"] = waiting.Count > limit,
                });
            }
            case ("GET", "/requests"):
                return Json(new JsonArray([.. _requests.Where(pair => pair.Key.Household == household && Waiting(pair.Key, pair.Value))
                    .OrderBy(pair => pair.Value.Created)
                    .Select(pair => (JsonNode)new JsonObject
                    {
                        ["device"] = pair.Key.Device, ["sign"] = pair.Value.Sign, ["dh"] = pair.Value.Dh, ["created"] = pair.Value.Created,
                        ["account"] = pair.Value.Account, ["approver"] = pair.Value.Approver, ["commit"] = pair.Value.Commit,
                        ["nonce"] = pair.Value.Nonce, ["reveal"] = pair.Value.Reveal,
                    })]));
            case ("POST", var committing) when RequestStep().Match(committing) is { Success: true } step:
            {
                var device = step.Groups[1].Value;
                var value = JsonNode.Parse(body)?[step.Groups[2].Value == "commit" ? "commit" : "nonce"] is { } given ? (string?)given : null;
                if (step.Groups[2].Value == "approve") return Approve(household, device, caller, list, body);
                if (value is null || Decode(value).Length != 32) return Error(400, "It must be 32 bytes, as base64url.");
                if (!_requests.TryGetValue((household, device), out var asked) || !Waiting((household, device), asked))
                {
                    return Error(404, "That PC isn't waiting to join this household.");
                }
                if (step.Groups[2].Value == "commit")
                {
                    if (asked.Approver is null) _requests[(household, device)] = asked with { Approver = caller, Commit = value };
                    else if (asked.Approver != caller || asked.Commit != value) return Error(409, "A member has already committed to approving this PC.");
                    return Ok();
                }
                if (asked.Approver != caller) return Error(403, "Only the member that committed can reveal.");
                if (asked.Nonce is null) return Error(409, "The waiting PC hasn't sent its nonce yet.");
                if (asked.Reveal is null) _requests[(household, device)] = asked with { Reveal = value };
                else if (asked.Reveal != value) return Error(409, "The approver has already revealed.");
                return Ok();
            }
            case ("DELETE", var denying) when denying.StartsWith("/requests/", StringComparison.Ordinal):
            {
                var device = denying["/requests/".Length..];
                if (!_requests.TryGetValue((household, device), out var asked) || !Unlapsed(asked)) return Error(404, "That PC isn't waiting to join this household.");
                if (asked.ApprovedEpoch is not null) return Error(409, "That PC has already been approved.");
                _requests.Remove((household, device));
                return Ok();
            }
            default:
                return Error(404, "Not found.");
        }
    }

    /// <summary>N2's POST /v1/auth/signin, with the fake's ID tokens: the nonce must be bound to the signing PC.</summary>
    private HttpResponseMessage SignIn(HttpRequestMessage request, byte[] body)
    {
        var posted = JsonNode.Parse(body)!;
        var sign = (string)posted["sign"]!;
        if (Authenticate(request, body, device => HouseholdCrypto.DeviceIdOf(Decode(sign)) == device ? (sign, false) : null) is { } refused) return refused;
        var device = request.Headers.GetValues("X-PL-Device").Single();
        var provider = (string)posted["provider"]!;
        var parts = ((string)posted["idToken"]!).Split('|');
        if (parts.Length != 3 || parts[0] != provider) return Error(401, "The ID token isn't one the provider signed.");
        if (parts[2] != BoundNonce(device, (string)posted["nonce"]!)) return Error(401, "The ID token's nonce doesn't match.");
        if (!_accounts.TryGetValue((provider, parts[1]), out var account)) _accounts[(provider, parts[1])] = account = Guid.NewGuid().ToString("N");
        foreach (var old in _sessions.Where(pair => pair.Value.Device == device).Select(pair => pair.Key).ToList()) _sessions.Remove(old);
        var token = Encode(RandomNumberGenerator.GetBytes(32));
        _sessions[token] = new Session(account, device, sign, (string)posted["dh"]!);
        return Json(new JsonObject
        {
            ["session"] = token, ["account"] = account, ["householdId"] = _links.GetValueOrDefault(account), ["hasRecovery"] = _recovery.ContainsKey(account),
        });
    }

    /// <summary>N2's account routes: a session, and the request signed by the PC it was given to.</summary>
    private HttpResponseMessage Account(HttpRequestMessage request, string method, string path, byte[] body)
    {
        var header = request.Headers.TryGetValues("Authorization", out var values) ? values.Single() : "";
        var token = header.StartsWith("Session ", StringComparison.Ordinal) ? header["Session ".Length..] : "";
        if (path == "/v1/auth/signout" && !_sessions.ContainsKey(token)) return Ok();
        if (!_sessions.TryGetValue(token, out var session)) return Error(401, "This session has ended: sign in again.");
        if (Authenticate(request, body, device => device == session.Device ? (session.Sign, false) : null) is { } refused) return refused;
        bool IsMember(string household) =>
            _households.TryGetValue(household, out var members) && members.TryGetValue(session.Device, out var member) && member.Removed is null;
        switch (method, path)
        {
            case ("POST", "/v1/account/household"):
            {
                if (JsonNode.Parse(body)?["householdId"] is not { } given) return Error(400, "householdId is required.");
                var named = (string)given!;
                if (!IsMember(named)) return Error(403, "This PC isn't a member of that household.");
                _links.TryAdd(session.Account, named);
                return _links[session.Account] == named ? Json(new JsonObject { ["ok"] = true, ["householdId"] = named }) : Error(409, "Linked to another.");
            }
            case ("POST", "/v1/account/requests"):
            {
                if (!_links.TryGetValue(session.Account, out var household)) return Error(409, "This account isn't linked to a household yet.");
                if (IsMember(household)) return Error(409, "This PC is already in the household.");
                _requests[(household, session.Device)] = new JoinRequest(session.Account, session.Sign, session.Dh, Now);
                return Json(new JsonObject { ["ok"] = true, ["householdId"] = household });
            }
            case ("GET", "/v1/account/requests"):
                return Json(new JsonObject
                {
                    ["requests"] = new JsonArray([.. _requests
                        .Where(pair => pair.Key.Device == session.Device && pair.Value.Account == session.Account && Unlapsed(pair.Value))
                        .OrderBy(pair => pair.Value.Created)
                        .Select(pair => (JsonNode)new JsonObject
                        {
                            ["device"] = session.Device,
                            ["household"] = pair.Key.Household,
                            ["approver"] = pair.Value.Approver is { } approver && _households[pair.Key.Household].TryGetValue(approver, out var member)
                                ? new JsonObject { ["device"] = approver, ["sign"] = member.Sign, ["dh"] = member.Dh }
                                : null,
                            ["commit"] = pair.Value.Commit,
                            ["reveal"] = pair.Value.Reveal,
                            ["approved"] = pair.Value.ApprovedEpoch is { } epoch ? new JsonObject { ["epoch"] = epoch } : null,
                            ["expires"] = pair.Value.ApprovedAt is { } at ? at + (long)TimeSpan.FromDays(7).TotalMilliseconds
                                : pair.Value.Created + (long)TimeSpan.FromDays(1).TotalMilliseconds,
                        })]),
                });
            case ("DELETE", "/v1/account/requests"):
                foreach (var key in _requests.Where(pair => pair.Key.Device == session.Device && pair.Value.Account == session.Account).Select(pair => pair.Key).ToList())
                {
                    _requests.Remove(key);
                }
                return Ok();
            case ("POST", "/v1/account/requests/nonce"):
            {
                var nonce = (string?)JsonNode.Parse(body)?["nonce"];
                if (nonce is null || Decode(nonce).Length != 32) return Error(400, "nonce must be 32 bytes, as base64url.");
                if (!_links.TryGetValue(session.Account, out var household) || !_requests.TryGetValue((household, session.Device), out var asked)
                    || !Waiting((household, session.Device), asked) || asked.Account != session.Account)
                {
                    return Error(404, "This PC has no request waiting.");
                }
                if (asked.Commit is null) return Error(409, "No member has committed to approving this PC yet.");
                if (asked.Nonce is null) _requests[(household, session.Device)] = asked with { Nonce = nonce };
                else if (asked.Nonce != nonce) return Error(409, "This PC has already sent its nonce.");
                return Ok();
            }
            case ("PUT", "/v1/account/recovery"):
            {
                var posted = JsonNode.Parse(body)!;
                if (!_links.TryGetValue(session.Account, out var household)) return Error(409, "This account isn't linked to a household yet.");
                if (!IsMember(household)) return Error(403, "Only a PC in the household can set how to recover it.");
                var current = _epochs.GetValueOrDefault(household, 1);
                if ((int?)posted["epoch"] != current) return Error(409, $"The household's key is at epoch {current}; seal the recovery at that one.");
                var replace = (bool?)posted["replace"] ?? false;
                if (!replace && (!_recovery.TryGetValue(session.Account, out var held) || held.Holder != session.Device))
                {
                    return Error(409, "This PC doesn't hold this account's recovery code; a new code must replace it.");
                }
                _recovery[session.Account] = new Recovery((string)posted["body"]!, (string)posted["verifier"]!, current, session.Device);
                return Ok();
            }
            case ("GET", "/v1/account/recovery"):
                return _recovery.TryGetValue(session.Account, out var kept)
                    ? Json(new JsonObject { ["body"] = kept.Body, ["epoch"] = kept.Epoch, ["holder"] = kept.Holder })
                    : Error(404, "This account has no recovery.");
            case ("POST", "/v1/account/recover"):
            {
                var shown = (string?)JsonNode.Parse(body)!["verifier"] ?? "";
                if (_recovered.TryGetValue((session.Account, shown), out var before) && before.Device == session.Device && Now - before.Used < 600_000)
                {
                    return Json(new JsonObject { ["household"] = before.Household, ["epoch"] = before.Epoch });   // a retry, its answer lost
                }
                if (!_recovery.TryGetValue(session.Account, out var envelope) || !_links.TryGetValue(session.Account, out var household)
                    || !_households[household].TryGetValue(envelope.Holder, out var holder) || holder.Removed is not null)
                {
                    return Error(404, "This account has nothing to recover.");
                }
                if ((string?)JsonNode.Parse(body)!["verifier"] != envelope.Verifier) return Error(403, "That isn't this account's recovery verifier.");
                var members = _households[household];
                var current = _epochs.GetValueOrDefault(household, 1);
                var others = members.Where(pair => pair.Key != session.Device && pair.Value.Removed is null).Select(pair => pair.Key).ToHashSet();
                foreach (var key in _requests.Where(pair => pair.Key.Household == household
                    && (pair.Key.Device == session.Device || others.Contains(pair.Key.Device) || (pair.Value.Approver is { } by && others.Contains(by))))
                    .Select(pair => pair.Key).ToList())
                {
                    _requests.Remove(key);
                }
                foreach (var other in others) members[other] = members[other] with { Removed = Now, RemovedEpoch = current };
                if (!members.TryGetValue(session.Device, out var mine) || mine.Removed is not null)
                {
                    members[session.Device] = new Member(session.Sign, session.Dh, Now, null, current);
                }
                foreach (var account in _links.Where(pair => pair.Value == household).Select(pair => pair.Key).ToList()) _recovery.Remove(account);
                _recovered[(session.Account, shown)] = (session.Device, household, current, Now);
                return Json(new JsonObject { ["household"] = household, ["epoch"] = current });
            }
            case ("POST", "/v1/auth/signout"):
                _sessions.Remove(token);
                foreach (var key in _requests.Where(pair => pair.Key.Device == session.Device && pair.Value.Account == session.Account).Select(pair => pair.Key).ToList())
                {
                    _requests.Remove(key);
                }
                return Ok();
            case ("DELETE", "/v1/account"):
                foreach (var key in _requests.Where(pair => pair.Value.Account == session.Account).Select(pair => pair.Key).ToList()) _requests.Remove(key);
                _recovery.Remove(session.Account);
                _links.Remove(session.Account);
                foreach (var key in _sessions.Where(pair => pair.Value.Account == session.Account).Select(pair => pair.Key).ToList()) _sessions.Remove(key);
                foreach (var key in _accounts.Where(pair => pair.Value == session.Account).Select(pair => pair.Key).ToList()) _accounts.Remove(key);
                return Ok();
            default:
                return Error(404, "Not found.");
        }
    }

    private HttpResponseMessage Create(HttpRequestMessage request, byte[] body)
    {
        var posted = JsonNode.Parse(body)!;
        var id = (string)posted["id"]!;
        var sign = (string)posted["sign"]!;
        if (Authenticate(request, body, device => HouseholdCrypto.DeviceIdOf(Decode(sign)) == device ? (sign, false) : null) is { } refused) return refused;
        var device = request.Headers.GetValues("X-PL-Device").Single();
        if (_households.TryGetValue(id, out var existing))
        {
            return existing.TryGetValue(device, out var member) && member.Removed is null ? Ok() : Error(409, "A household with this ID already exists.");
        }
        _households[id] = new Dictionary<string, Member> { [device] = new Member(sign, (string)posted["dh"]!, Now, null, 1) };
        _epochs[id] = 1;
        return Ok();
    }

    private HttpResponseMessage Meeting(string method, string meeting, string slot, byte[] body)
    {
        var now = clock.GetUtcNow();
        var created = _meetings.Where(pair => pair.Key.Meeting == meeting).Select(pair => (DateTimeOffset?)pair.Value.Created).Min() ?? now;
        var ended = now - created >= TimeSpan.FromMinutes(10);
        if (method == "PUT")
        {
            if (body.Length == 0) return Error(400, "A meeting slot can't be empty.");
            if (body.Length > 8 * 1024) return Error(413, "A meeting slot holds at most 8 KB.");
            if (ended) return Error(410, "This meeting has ended.");
            if (_meetings.ContainsKey((meeting, slot))) return Error(409, "This slot has already been written.");
            _meetings[(meeting, slot)] = (body, created);
            return Ok();
        }
        if (ended || !_meetings.TryGetValue((meeting, slot), out var kept)) return Error(404, "Nothing is in this slot.");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(kept.Body) };
    }

    /// <summary>The Worker's checks, in its order; null when the request passes. <paramref name="keyOf"/> gives the key the
    /// device must have signed with and whether it was removed, or null when it isn't one the route knows.</summary>
    private HttpResponseMessage? Authenticate(HttpRequestMessage request, byte[] body, Func<string, (string Sign, bool Removed)?> keyOf)
    {
        string? Header(string name) => request.Headers.TryGetValues(name, out var values) ? values.Single() : null;
        var device = Header("X-PL-Device");
        var time = Header("X-PL-Time");
        var signature = Header("X-PL-Signature") is { } text ? Decode(text) : null;
        if (device is null || time is null || signature is not { Length: 64 }) return Error(401, "This request needs the X-PL headers.");
        if (Math.Abs(clock.GetUtcNow().ToUnixTimeSeconds() - long.Parse(time)) > 300) return Error(401, "This request's time is more than 5 minutes off.");
        if (keyOf(device) is not { } key) return Error(401, "This request isn't signed by a member.");
        var signed = HouseholdCrypto.RequestToSign(request.Method.Method, request.RequestUri!.PathAndQuery, long.Parse(time), body);
        if (!HouseholdCrypto.Verify(Decode(key.Sign), signed, signature)) return Error(401, "This request isn't signed by a member.");
        if (!_seen.Add(Convert.ToHexString(signature.AsSpan(0, 32)))) return Error(401, "This request has already been made.");
        return key.Removed ? Error(410, "This PC was removed from this household.") : null;
    }

    /// <summary>Removes a member at the household's epoch, as the Worker does: accounts are left alone but for a recovery the
    /// removed PC holds, its request to join goes, and when no member is left the household ends, its members kept as
    /// removed while its batches, keys, requests, links and recoveries go.</summary>
    private void RemoveMember(string household, string device)
    {
        var list = _households[household];
        if (list[device].Removed is not null) return;
        list[device] = list[device] with { Removed = Now, RemovedEpoch = _epochs.GetValueOrDefault(household, 1) };
        var linked = _links.Where(pair => pair.Value == household).Select(pair => pair.Key).ToHashSet();
        foreach (var account in _recovery.Where(pair => pair.Value.Holder == device && linked.Contains(pair.Key)).Select(pair => pair.Key).ToList())
        {
            _recovery.Remove(account);
        }
        _requests.Remove((household, device));
        foreach (var key in _requests.Where(pair => pair.Key.Household == household && pair.Value.Approver == device && pair.Value.ApprovedEpoch is null)
            .Select(pair => pair.Key).ToList())
        {
            _requests.Remove(key);                                             // those it committed to: their PCs may ask again
        }
        if (list.Values.Any(member => member.Removed is null)) return;
        foreach (var account in linked)
        {
            _recovery.Remove(account);
            _links.Remove(account);
        }
        foreach (var key in _requests.Keys.Where(key => key.Household == household).ToList()) _requests.Remove(key);
        foreach (var key in _envelopes.Keys.Where(key => key.Household == household).ToList()) _envelopes.Remove(key);
        _batches.RemoveAll(batch => batch.Household == household);
    }

    /// <summary>A request still waiting: its account still linked to its household, not approved, less than 24 hours old.</summary>
    private bool Waiting((string Household, string Device) key, JoinRequest request) =>
        request.ApprovedEpoch is null && Now - request.Created < (long)TimeSpan.FromDays(1).TotalMilliseconds
        && _links.TryGetValue(request.Account, out var linked) && linked == key.Household;

    /// <summary>A request not yet lapsed: waiting for less than 24 hours, or approved less than 7 days ago.</summary>
    private bool Unlapsed(JoinRequest request) => request.ApprovedAt is { } at
        ? Now - at < (long)TimeSpan.FromDays(7).TotalMilliseconds
        : Now - request.Created < (long)TimeSpan.FromDays(1).TotalMilliseconds;

    /// <summary>POST …/requests/{device}/approve, as the Worker takes it (plan 0.9).</summary>
    private HttpResponseMessage Approve(string household, string device, string caller, Dictionary<string, Member> list, byte[] body)
    {
        var posted = JsonNode.Parse(body)!;
        var epoch = (int)posted["epoch"]!;
        var sealedBody = (string)posted["body"]!;
        if (sealedBody.Length > 16384) return Error(400, "The body must be {\"epoch\",\"body\"}, 16384 characters at most.");
        if (!_requests.TryGetValue((household, device), out var request) || !Waiting((household, device), request))
        {
            if (request is not { ApprovedEpoch: { } approvedAt }) return Error(404, "That PC isn't waiting to join this household.");
            return request.Approver == caller && approvedAt == epoch && _envelopes.TryGetValue((household, epoch, device), out var made)
                && made.From == caller && made.Body == sealedBody
                ? Ok()
                : Error(409, "That PC has already been approved.");
        }
        if (request.Approver != caller) return Error(403, "Only the member that committed can approve.");
        if (request.Reveal is null) return Error(409, "The approval's reveal hasn't been made yet.");
        var current = _epochs.GetValueOrDefault(household, 1);
        if (epoch != current) return Error(409, $"The household's key is at epoch {current}; approve with that one.");
        if (_envelopes.ContainsKey((household, epoch, device))) return Error(409, "That PC already has a key at this epoch.");
        if (!list.TryGetValue(device, out var already) || already.Removed is not null)
        {
            if (list.Values.Count(member => member.Removed is null) >= 16) return Error(409, "This household already has 16 PCs.");
            list[device] = new Member(request.Sign, request.Dh, Now, null, current);
        }
        _envelopes[(household, epoch, device)] = (caller, sealedBody);
        _requests[(household, device)] = request with { ApprovedEpoch = epoch, ApprovedAt = Now };
        return Ok();
    }

    private static HttpResponseMessage Ok() => Json(new JsonObject { ["ok"] = true });

    internal static HttpResponseMessage Json(JsonNode node) =>
        new(HttpStatusCode.OK) { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };

    internal static HttpResponseMessage Error(int status, string message) =>
        new((HttpStatusCode)status) { Content = new StringContent(JsonSerializer.Serialize(new { error = message }), Encoding.UTF8, "application/json") };

    /// <summary>An error with the household's epoch as it is, as every 409 on new keys carries it (plan 0.10).</summary>
    internal static HttpResponseMessage Error(int status, string message, int epoch) =>
        new((HttpStatusCode)status) { Content = new StringContent(JsonSerializer.Serialize(new { error = message, epoch }), Encoding.UTF8, "application/json") };

    private static string Encode(byte[] bytes) => Base64Url.EncodeToString(bytes);

    private static byte[] Decode(string text) => Base64Url.DecodeFromChars(text);

    [GeneratedRegex("^/v1/households/([0-9a-f]{32})(/.*)$")]
    private static partial Regex HouseholdPath();

    [GeneratedRegex("^/v1/meetings/([0-9a-f]{32})/(adder|joiner|answer|welcome|joined|welcomed)$")]
    private static partial Regex MeetingPath();

    [GeneratedRegex("^/requests/([0-9a-f]{32})/(commit|reveal|approve)$")]
    private static partial Regex RequestStep();

    public sealed record Member(string Sign, string Dh, long Added, long? Removed, int AddedEpoch = 1, int? RemovedEpoch = null);

    /// <summary>An account's recovery: its sealed body, its verifier, the epoch it was put at and the PC that holds its code.</summary>
    public sealed record Recovery(string Body, string Verifier, int Epoch, string Holder);

    private sealed record Batch(string Household, long Seq, string Device, int Epoch, long DeviceSeq, byte[] Body, string Sig);

    private sealed record Session(string Account, string Device, string Sign, string Dh);

    /// <summary>A PC's request to join, with its approval so far (plan 0.9).</summary>
    public sealed record JoinRequest(
        string Account, string Sign, string Dh, long Created, string? Approver = null, string? Commit = null, string? Nonce = null, string? Reveal = null,
        int? ApprovedEpoch = null, long? ApprovedAt = null);
}
