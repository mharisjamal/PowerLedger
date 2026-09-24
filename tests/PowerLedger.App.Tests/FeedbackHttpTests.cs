using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>Posts a real report over a real loopback HttpListener (no fake HttpMessageHandler), to check the exact JSON
/// shape the Worker's feedback route matches against (review round): every field, an image's name, content type and
/// base64, byte for byte.</summary>
public class FeedbackHttpTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 14, 32, 0, TimeSpan.Zero);
    private static readonly Regex AppPattern = new("^[0-9A-Za-z][0-9A-Za-z.+_-]{0,63}$");

    [Fact]
    public async Task The_posted_body_matches_the_workers_own_shape()
    {
        var port = FreePort();
        using var server = new HttpListener();
        server.Prefixes.Add($"http://127.0.0.1:{port}/");
        server.Start();
        var originalUrl = FeedbackEndpoint.Url;
        FeedbackEndpoint.Url = $"http://127.0.0.1:{port}/v1/feedback";
        try
        {
            var receiving = server.GetContextAsync();
            var (imageData, imageType) = ImageProcessing.Process(TestImages.Png(40, 40))!.Value;
            var report = new FeedbackReport(
                "It broke when I clicked Save", null, "0.7.0+abc1234", "Windows 11 Home 26200", "x64", "line one\nline two",
                [new FeedbackImagePayload("image-1.png", imageType, Convert.ToBase64String(imageData))]);
            var folder = Path.Combine(Path.GetTempPath(), "pl-feedback-http-tests", Guid.NewGuid().ToString("n"));
            var sender = new FeedbackSender(new HttpClient(), folder, new FakeTimeProvider(Now));

            var sendTask = sender.SendAsync(report);
            var context = await receiving;
            var requestBody = await new StreamReader(context.Request.InputStream).ReadToEndAsync();
            context.Response.StatusCode = (int)HttpStatusCode.Accepted;
            context.Response.Close();
            var result = await sendTask;

            result.Outcome.ShouldBe(FeedbackOutcome.Sent);
            context.Request.ContentType.ShouldStartWith("application/json");

            using var json = JsonDocument.Parse(requestBody);
            var root = json.RootElement;
            root.GetProperty("text").GetString().ShouldBe("It broke when I clicked Save");
            root.GetProperty("email").ValueKind.ShouldBe(JsonValueKind.Null);
            var app = root.GetProperty("app").GetString()!;
            AppPattern.IsMatch(app).ShouldBeTrue(app);
            root.GetProperty("os").GetString().ShouldBe("Windows 11 Home 26200");
            root.GetProperty("arch").GetString().ShouldBe("x64");
            root.GetProperty("log").GetString().ShouldBe("line one\nline two");
            var images = root.GetProperty("images");
            images.GetArrayLength().ShouldBe(1);
            var image = images[0];
            image.GetProperty("name").GetString().ShouldBe("image-1.png");
            image.GetProperty("contentType").GetString().ShouldBe("image/png");
            var decoded = Convert.FromBase64String(image.GetProperty("data").GetString()!);
            decoded.ShouldBe(imageData);
            // PNG magic: 89 50 4E 47.
            decoded[0].ShouldBe((byte)0x89);
            decoded[1].ShouldBe((byte)0x50);
            decoded[2].ShouldBe((byte)0x4E);
            decoded[3].ShouldBe((byte)0x47);
        }
        finally
        {
            FeedbackEndpoint.Url = originalUrl;
            server.Close();
        }
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
