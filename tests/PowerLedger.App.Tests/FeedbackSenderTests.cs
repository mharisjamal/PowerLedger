using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace PowerLedger.App.Tests;

public class FeedbackSenderTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 14, 32, 0, TimeSpan.Zero);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pl-feedback-sender-tests", Guid.NewGuid().ToString("n"));
    private readonly FakeHttp _http = new();
    private readonly FakeTimeProvider _clock = new(Now);

    private FeedbackSender Sender() => new(_http.Client(), _folder, _clock);

    private static FeedbackReport Report(string text = "It broke") => new(text, null, "0.7.0+abc1234", "Windows 11 Home 26200", "x64", null, []);

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private static byte[] ErrorBody(string message) => Encoding.UTF8.GetBytes($$"""{"error":"{{message}}"}""");

    [Fact]
    public async Task A_202_is_sent()
    {
        _http.Reply(HttpStatusCode.Accepted, []);

        var result = await Sender().SendAsync(Report());

        result.Outcome.ShouldBe(FeedbackOutcome.Sent);
        result.Message.ShouldBe("Thanks, sent.");
    }

    [Fact]
    public async Task A_400_is_refused_in_the_servers_own_words()
    {
        _http.Reply(HttpStatusCode.BadRequest, ErrorBody("Text is required."));

        var result = await Sender().SendAsync(Report());

        result.Outcome.ShouldBe(FeedbackOutcome.Failed);
        result.Message.ShouldBe("Text is required.");
    }

    /// <summary>413, over the Worker's 8 MB body limit, is refused the same way a 400 is.</summary>
    [Fact]
    public async Task A_413_is_refused_in_the_servers_own_words()
    {
        _http.Reply(HttpStatusCode.RequestEntityTooLarge, ErrorBody("The report is too large."));

        var result = await Sender().SendAsync(Report());

        result.Outcome.ShouldBe(FeedbackOutcome.Failed);
        result.Message.ShouldBe("The report is too large.");
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task A_429_or_503_is_kept_pending_for_a_retry(HttpStatusCode status)
    {
        _http.Reply(status, []);

        var result = await Sender().SendAsync(Report());

        result.Outcome.ShouldBe(FeedbackOutcome.Pending);
        FeedbackQueue.ReadAll(_folder).Count.ShouldBe(1);
    }

    [Fact]
    public async Task No_network_is_kept_pending_for_a_retry()
    {
        _http.Answer = (_, _) => throw new HttpRequestException("no network");

        var result = await Sender().SendAsync(Report());

        result.Outcome.ShouldBe(FeedbackOutcome.Pending);
        FeedbackQueue.ReadAll(_folder).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_retry_that_finally_gets_a_202_deletes_the_pending_file()
    {
        FeedbackQueue.Save(_folder, Report(), _clock.GetUtcNow());
        _http.Reply(HttpStatusCode.Accepted, []);

        await Sender().RetryPendingAsync();

        FeedbackQueue.ReadAll(_folder).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_retry_that_is_refused_gives_up_and_deletes_it_too()
    {
        FeedbackQueue.Save(_folder, Report(), _clock.GetUtcNow());
        _http.Reply(HttpStatusCode.BadRequest, ErrorBody("no good"));

        await Sender().RetryPendingAsync();

        FeedbackQueue.ReadAll(_folder).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_retry_that_still_fails_keeps_the_pending_file()
    {
        FeedbackQueue.Save(_folder, Report(), _clock.GetUtcNow());
        _http.Reply(HttpStatusCode.ServiceUnavailable, []);

        await Sender().RetryPendingAsync();

        FeedbackQueue.ReadAll(_folder).Count.ShouldBe(1);
        _http.Requests.Count.ShouldBe(1);   // it was tried, just not sent
    }

    [Fact]
    public async Task A_report_older_than_30_days_is_given_up_on_without_even_trying()
    {
        FeedbackQueue.Save(_folder, Report(), _clock.GetUtcNow());
        _clock.Advance(FeedbackQueue.MaxAge + TimeSpan.FromDays(1));

        await Sender().RetryPendingAsync();

        FeedbackQueue.ReadAll(_folder).ShouldBeEmpty();
        _http.Requests.ShouldBeEmpty();
    }
}
