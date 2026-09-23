using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using PowerLedger.Contracts;
using PowerLedger.Service.Sharing;
using Shouldly;

namespace PowerLedger.Service.Tests;

/// <summary>How the service talks to the data server (data-sharing design §4), against a fake handler.</summary>
public class SharingClientTests
{
    private const string Key = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static readonly Uri Endpoint = new("http://127.0.0.1:8766/");

    [Theory]
    [InlineData(HttpStatusCode.OK, "Accepted", null)]
    [InlineData(HttpStatusCode.BadRequest, "Rejected", "/power/minutes: the minutes must rise")]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "Rejected", "The report is over 1 MB")]
    [InlineData(HttpStatusCode.Gone, "Gone", null)]
    [InlineData(HttpStatusCode.Unauthorized, "Refused", "A bearer key is needed")]
    [InlineData(HttpStatusCode.Forbidden, "Refused", "That key isn't this install's")]
    [InlineData(HttpStatusCode.TooManyRequests, "Refused", "Too many requests today")]
    [InlineData(HttpStatusCode.InternalServerError, "Refused", "Something went wrong")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "Refused", "Something went wrong")]
    public async Task Each_answer_from_the_server_is_an_outcome_with_the_servers_reason(HttpStatusCode status, string outcome, string? reason)
    {
        var handler = new FakeHandler(_ => Answer(status, reason is null ? "{\"ok\":true}" : $"{{\"error\":\"{reason}.\"}}"));
        using var client = new SharingClient(Endpoint, handler);

        var sent = await client.SendReportAsync(SharingClient.Gzip("{}"u8.ToArray()), Key);

        sent.GetType().Name.ShouldBe(outcome);
        sent.Reason.ShouldBe(reason);                                        // the server's sentence, without its full stop
    }

    [Fact]
    public async Task An_answer_without_the_servers_error_still_says_what_happened()
    {
        using var client = new SharingClient(Endpoint, new FakeHandler(_ => Answer(HttpStatusCode.BadGateway, "<html>bad gateway</html>")));

        var sent = await client.SendReportAsync(SharingClient.Gzip("{}"u8.ToArray()), Key);

        sent.ShouldBeOfType<SendOutcome.Refused>().Reason.ShouldBe("the server answered 502 Bad Gateway");
    }

    [Fact]
    public async Task A_server_that_cannot_be_reached_or_does_not_answer_in_time_is_unreachable()
    {
        using var down = new SharingClient(Endpoint, new FakeHandler(_ => throw new HttpRequestException("No connection could be made.")));
        (await down.SendReportAsync(SharingClient.Gzip("{}"u8.ToArray()), Key))
            .ShouldBeOfType<SendOutcome.Unreachable>().Reason.ShouldBe("the server couldn't be reached (No connection could be made)");

        using var slow = new SharingClient(Endpoint, new FakeHandler(async (_, cancel) =>
        {
            await Task.Delay(Timeout.Infinite, cancel);
            return Answer(HttpStatusCode.OK, "{}");
        }), timeout: TimeSpan.FromMilliseconds(50));
        (await slow.DeleteAsync(SharingFakes.InstallId, Key))
            .ShouldBeOfType<SendOutcome.Unreachable>().Reason.ShouldBe("the server didn't answer in time");
    }

    [Fact]
    public async Task Stopping_the_service_cancels_a_request_rather_than_calling_it_unreachable()
    {
        using var client = new SharingClient(Endpoint, new FakeHandler(async (_, cancel) =>
        {
            await Task.Delay(Timeout.Infinite, cancel);
            return Answer(HttpStatusCode.OK, "{}");
        }));
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Should.ThrowAsync<OperationCanceledException>(() => client.SendConsentAsync(SharingFakes.InstallId, Key, SharingFakes.AllOn, stop.Token));
    }

    [Fact]
    public async Task A_report_goes_as_gzip_json_with_the_install_key_and_the_services_name()
    {
        var handler = new FakeHandler(_ => Answer(HttpStatusCode.OK, "{\"ok\":true}"));
        using var client = new SharingClient(Endpoint, handler);
        var report = ReportJson.Write(ReportBuilder.Build(SharingFakes.Inputs()));

        (await client.SendReportAsync(SharingClient.Gzip(report), Key)).ShouldBeOfType<SendOutcome.Accepted>();

        var (request, body) = handler.Seen.ShouldHaveSingleItem();
        (request.Method, request.RequestUri).ShouldBe((HttpMethod.Post, new Uri("http://127.0.0.1:8766/v1/report")));
        request.Headers.Authorization.ShouldNotBeNull().ToString().ShouldBe($"Bearer {Key}");
        request.Headers.UserAgent.ToString().ShouldBe($"PowerLedger/{ServiceVersion.Short}");
        request.Content.ShouldNotBeNull().Headers.ContentEncoding.ShouldBe(new[] { "gzip" });
        request.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe("application/json");
        Gunzip(body).ShouldBe(report);
    }

    [Fact]
    public async Task A_consent_change_goes_as_the_id_and_the_four_switches()
    {
        var handler = new FakeHandler(_ => Answer(HttpStatusCode.OK, "{\"ok\":true}"));
        using var client = new SharingClient(Endpoint, handler);

        (await client.SendConsentAsync(SharingFakes.InstallId, Key, new Consent(ConsentText.Version, true, false, true, false)))
            .ShouldBeOfType<SendOutcome.Accepted>();

        var (request, body) = handler.Seen.ShouldHaveSingleItem();
        request.RequestUri.ShouldBe(new Uri("http://127.0.0.1:8766/v1/consent"));
        request.Headers.Authorization.ShouldNotBeNull().ToString().ShouldBe($"Bearer {Key}");
        request.Content.ShouldNotBeNull().Headers.ContentEncoding.ShouldBeEmpty();
        JsonNode.DeepEquals(JsonNode.Parse(body), JsonNode.Parse($$$"""
            {"installId":"{{{SharingFakes.InstallId}}}","consent":{"version":1,"diagnostics":true,"usage":false,"power":true,"share":false}}
            """)).ShouldBeTrue(Encoding.UTF8.GetString(body));
    }

    [Fact]
    public async Task Deleting_goes_as_the_id_alone()
    {
        var handler = new FakeHandler(_ => Answer(HttpStatusCode.OK, "{\"ok\":true}"));
        using var client = new SharingClient(Endpoint, handler);

        (await client.DeleteAsync(SharingFakes.InstallId, Key)).ShouldBeOfType<SendOutcome.Accepted>();

        var (request, body) = handler.Seen.ShouldHaveSingleItem();
        request.RequestUri.ShouldBe(new Uri("http://127.0.0.1:8766/v1/delete"));
        JsonNode.DeepEquals(JsonNode.Parse(body), JsonNode.Parse($"{{\"installId\":\"{SharingFakes.InstallId}\"}}")).ShouldBeTrue();
    }

    [Theory]
    [InlineData("http://127.0.0.1:8766", "http://127.0.0.1:8766/")]
    [InlineData("http://localhost:8766/feed/", "http://localhost:8766/feed/")]
    [InlineData("https://[::1]:8443", "https://[::1]:8443/")]
    [InlineData("https://powerledger.example.com/", null)]
    [InlineData("http://127.0.0.1.example.com/", null)]
    [InlineData(@"file:///C:/Windows/", null)]
    [InlineData("not an address", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void The_registry_can_point_the_service_at_a_stand_in_on_this_machine_and_nowhere_else(string? value, string? used) =>
        SharingEndpoint.Resolve(() => value).ShouldBe(used is null ? SharingEndpoint.BuiltIn : new Uri(used));

    [Fact]
    public void A_registry_that_cannot_be_read_leaves_the_built_in_server() =>
        SharingEndpoint.Resolve(() => throw new UnauthorizedAccessException()).ShouldBe(SharingEndpoint.BuiltIn);

    private static HttpResponseMessage Answer(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static byte[] Gunzip(byte[] body)
    {
        using var unpacked = new MemoryStream();
        using (var gzip = new GZipStream(new MemoryStream(body), CompressionMode.Decompress)) gzip.CopyTo(unpacked);
        return unpacked.ToArray();
    }

    /// <summary>Answers each request as told, keeping what it was sent.</summary>
    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> answer)
            : this((request, _) => Task.FromResult(answer(request)))
        {
        }

        public List<(HttpRequestMessage Request, byte[] Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            lock (Seen) Seen.Add((request, body));
            return await answer(request, cancellationToken);
        }
    }
}
