using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace PowerLedger.App;

/// <summary>What sending, or retrying, a feedback report came to.</summary>
internal enum FeedbackOutcome
{
    Sent,
    Pending,
    Failed,
}

internal sealed record FeedbackResult(FeedbackOutcome Outcome, string Message)
{
    public static readonly FeedbackResult Sent = new(FeedbackOutcome.Sent, "Thanks, sent.");
    public static readonly FeedbackResult Pending = new(FeedbackOutcome.Pending, "Saved. It's sent when this PC is online.");
}

/// <summary>Posts feedback to <see cref="FeedbackEndpoint"/> (spec's feedback feature): 202 is sent; 400 is refused, in
/// the server's own words; 429, 503 or no network at all is saved to <see cref="FeedbackQueue"/> for a retry. Also
/// retries whatever is already saved there, on its own schedule.</summary>
internal sealed class FeedbackSender(HttpClient http, string pendingFolder, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task<FeedbackResult> SendAsync(FeedbackReport report, CancellationToken cancel = default)
    {
        var (result, message) = await PostAsync(report, cancel).ConfigureAwait(false);
        return result switch
        {
            PostResult.Sent => FeedbackResult.Sent,
            PostResult.Refused => new FeedbackResult(FeedbackOutcome.Failed, message ?? "The report was refused."),
            _ => SaveAndReportPending(report),
        };
    }

    /// <summary>Every report under Feedback\pending: sent and deleted on 202, given up on and deleted on 400 or once
    /// <see cref="FeedbackQueue.MaxAge"/> old, otherwise left for the next retry (review round: 1 minute after start,
    /// then hourly — App.xaml.cs's own timer, at the update check's cadence).</summary>
    public async Task RetryPendingAsync(CancellationToken cancel = default)
    {
        foreach (var (path, pending) in FeedbackQueue.ReadAll(pendingFolder))
        {
            if (clock.GetUtcNow() - pending.SavedAt > FeedbackQueue.MaxAge)
            {
                FeedbackQueue.Delete(path);
                continue;
            }
            var (result, _) = await PostAsync(pending.Report, cancel).ConfigureAwait(false);
            if (result is PostResult.Sent or PostResult.Refused) FeedbackQueue.Delete(path);
        }
    }

    private FeedbackResult SaveAndReportPending(FeedbackReport report)
    {
        FeedbackQueue.Save(pendingFolder, report, clock.GetUtcNow());
        return FeedbackResult.Pending;
    }

    private enum PostResult
    {
        Sent,
        Refused,
        Retryable,
    }

    private async Task<(PostResult Result, string? Message)> PostAsync(FeedbackReport report, CancellationToken cancel)
    {
        HttpResponseMessage response;
        try
        {
            var json = JsonSerializer.Serialize(report, Options);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            response = await http.PostAsync(FeedbackEndpoint.Url, content, cancel).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return (PostResult.Retryable, null);   // no network, or the server is unreachable
        }
        catch (TaskCanceledException) when (!cancel.IsCancellationRequested)
        {
            return (PostResult.Retryable, null);   // timed out on its own, not asked to stop
        }
        using (response)
        {
            // Worker's feedback route (review round, matched exactly): 202 sent; 400 or 413 (over 8 MB) refused, in
            // {"error"} JSON; 429 and 503 are both worth retrying later, the same as anything else unexpected — never
            // lost over a status this never saw coming.
            if (response.StatusCode == HttpStatusCode.Accepted) return (PostResult.Sent, null);
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge)
                return (PostResult.Refused, await ReadErrorAsync(response, cancel).ConfigureAwait(false));
            return (PostResult.Retryable, null);
        }
    }

    private sealed record ErrorBody(string? Error);

    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancel)
    {
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            return (await JsonSerializer.DeserializeAsync<ErrorBody>(body, Options, cancel).ConfigureAwait(false))?.Error;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException)
        {
            return null;
        }
    }
}
