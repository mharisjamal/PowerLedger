using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using PowerLedger.Sensors.Nut;

namespace PowerLedger.Sensors.Tests;

/// <summary>
/// A stand-in for NUT's upsd on the loopback interface, speaking the protocol's reading commands (RFC 9271): USERNAME,
/// PASSWORD, LIST VAR and LOGOUT. It serves the UPSes and variables a test gives it, and can be told to want a password,
/// to say its data is stale, to stop answering, or to drop every connection after one list.
/// </summary>
internal sealed class FakeNutServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accepting;
    private int _connections;

    public FakeNutServer()
    {
        _listener.Start();
        _accepting = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Each UPS's variables, by UPS name.</summary>
    public ConcurrentDictionary<string, Dictionary<string, string>> Upses { get; } = new(StringComparer.Ordinal);

    /// <summary>Every command received, in order, across connections.</summary>
    public ConcurrentQueue<string> Commands { get; } = new();

    public int Connections => Volatile.Read(ref _connections);

    /// <summary>A username and password the server wants before it lists anything; null lists to anybody.</summary>
    public (string User, string Password)? Wants { get; set; }

    public bool Stale { get; set; }

    /// <summary>Reads commands but never answers, as a server that has hung.</summary>
    public bool Silent { get; set; }

    /// <summary>Closes each connection once it has answered one list, as a server restarting between reads.</summary>
    public bool DropAfterList { get; set; }

    public FakeNutServer Serve(string ups, params (string Name, string Value)[] variables)
    {
        Upses[ups] = variables.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);
        return this;
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            await _accepting.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The listener was stopped under it.
        }
        _stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }
            Interlocked.Increment(ref _connections);
            _ = ServeAsync(client);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                string? user = null;
                var authenticated = Wants is null;
                while (await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false) is { } line)
                {
                    Commands.Enqueue(line);
                    if (Silent) continue;
                    var words = NutProtocol.Words(line);
                    var reply = new StringBuilder();
                    var drop = false;
                    switch (words)
                    {
                        case ["USERNAME", var name]:
                            user = name;
                            reply.Append("OK\n");
                            break;
                        case ["PASSWORD", var password]:
                            authenticated = Wants is not { } wanted || (wanted.User == user && wanted.Password == password);
                            reply.Append(authenticated ? "OK\n" : "ERR INVALID-PASSWORD\n");
                            break;
                        case ["LIST", "VAR", var ups]:
                            if (!authenticated) reply.Append("ERR ACCESS-DENIED\n");
                            else if (!Upses.TryGetValue(ups, out var variables)) reply.Append("ERR UNKNOWN-UPS\n");
                            else if (Stale) reply.Append("ERR DATA-STALE\n");
                            else
                            {
                                reply.Append($"BEGIN LIST VAR {ups}\n");
                                foreach (var (name, value) in variables) reply.Append($"VAR {ups} {name} {NutProtocol.Quote(value)}\n");
                                reply.Append($"END LIST VAR {ups}\n");
                                drop = DropAfterList;
                            }
                            break;
                        case ["LOGOUT"]:
                            reply.Append("OK Goodbye\n");
                            drop = true;
                            break;
                        default:
                            reply.Append("ERR UNKNOWN-COMMAND\n");
                            break;
                    }
                    var bytes = Encoding.UTF8.GetBytes(reply.ToString());
                    await stream.WriteAsync(bytes, _stop.Token).ConfigureAwait(false);
                    if (drop) return;
                }
            }
            catch (Exception)
            {
                // The client went, or the server is stopping.
            }
        }
    }
}
