using System.IO;
using System.Net;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace PowerLedger.App.Tests;

public class FeedbackViewModelTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 14, 32, 0, TimeSpan.Zero);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pl-feedback-vm-tests", Guid.NewGuid().ToString("n"));
    private readonly FakeHttp _http = new();
    private string? _log = "the log";

    private FeedbackViewModel Model() => new(new FeedbackSender(_http.Client(), _folder, new FakeTimeProvider(Now)), UiThreads.Inline, () => _log);

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void Text_longer_than_ten_thousand_characters_is_cut_off()
    {
        var model = Model();

        model.Text = new string('a', FeedbackViewModel.MaxTextLength + 500);

        model.Text.Length.ShouldBe(FeedbackViewModel.MaxTextLength);
    }

    [Fact]
    public void The_counter_shows_only_once_the_limit_is_close()
    {
        var model = Model();

        model.Text = new string('a', FeedbackViewModel.MaxTextLength - FeedbackViewModel.CounterThreshold - 1);
        model.ShowCounter.ShouldBeFalse();

        model.Text = new string('a', FeedbackViewModel.MaxTextLength - FeedbackViewModel.CounterThreshold);
        model.ShowCounter.ShouldBeTrue();
    }

    [Fact]
    public void Send_needs_some_text_first()
    {
        var model = Model();
        model.Send.CanExecute(null).ShouldBeFalse();

        model.Text = "It broke";
        model.Send.CanExecute(null).ShouldBeTrue();

        model.Text = "   ";
        model.Send.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public void At_most_five_images_can_be_attached()
    {
        var model = Model();
        for (var i = 0; i < FeedbackViewModel.MaxImages; i++) model.TryAddImage(TestImages.Png(20, 20)).ShouldBe(FeedbackAddImageResult.Added);

        model.CanAddMoreImages.ShouldBeFalse();
        model.TryAddImage(TestImages.Png(20, 20)).ShouldBe(FeedbackAddImageResult.TooMany);
        model.Images.Count.ShouldBe(FeedbackViewModel.MaxImages);
    }

    [Fact]
    public void Bytes_that_arent_a_picture_are_not_added()
    {
        var model = Model();

        model.TryAddImage([1, 2, 3]).ShouldBe(FeedbackAddImageResult.NotAnImage);

        model.Images.ShouldBeEmpty();
    }

    /// <summary>Review round: an image's name is never reused even once an earlier one is removed, since the Worker
    /// needs every name in a report unique.</summary>
    [Fact]
    public void A_removed_images_name_is_never_reused()
    {
        var model = Model();
        model.TryAddImage(TestImages.Png(20, 20));
        model.TryAddImage(TestImages.Png(20, 20));
        var first = model.Images[0];
        model.RemoveImage.Execute(first);

        model.TryAddImage(TestImages.Png(20, 20));

        model.Images.Select(i => i.Name).Distinct().Count().ShouldBe(model.Images.Count);
        model.Images.ShouldNotContain(i => i.Name == first.Name);
    }

    [Fact]
    public void Turning_the_log_tick_off_sends_no_log()
    {
        var model = Model();
        model.Text = "It broke";

        model.AttachLog = false;
        model.BuildReport().Log.ShouldBeNull();

        model.AttachLog = true;
        model.BuildReport().Log.ShouldBe("the log");
    }

    [Fact]
    public void A_blank_email_is_sent_as_null()
    {
        var model = Model();
        model.Text = "It broke";
        model.Email = "   ";

        model.BuildReport().Email.ShouldBeNull();
    }

    [Fact]
    public async Task Sending_successfully_closes_with_the_thank_you_message()
    {
        _http.Reply(HttpStatusCode.Accepted, []);
        var model = Model();
        model.Text = "It broke";
        string? closedWith = null;
        model.Closed += message => closedWith = message;

        model.Send.Execute(null);
        await WaitFor.True(() => closedWith is not null);

        closedWith.ShouldBe("Thanks, sent.");
    }

    [Fact]
    public async Task A_network_failure_saves_the_report_and_closes_saying_so()
    {
        _http.Answer = (_, _) => throw new System.Net.Http.HttpRequestException("no network");
        var model = Model();
        model.Text = "It broke";
        string? closedWith = null;
        model.Closed += message => closedWith = message;

        model.Send.Execute(null);
        await WaitFor.True(() => closedWith is not null);

        closedWith.ShouldBe("Saved. It's sent when this PC is online.");
        FeedbackQueue.ReadAll(_folder).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_400_refusal_shows_the_servers_message_and_stays_open()
    {
        _http.Reply(HttpStatusCode.BadRequest, System.Text.Encoding.UTF8.GetBytes("""{"error":"Text is required."}"""));
        var model = Model();
        model.Text = "It broke";
        var closed = false;
        model.Closed += _ => closed = true;

        model.Send.Execute(null);
        await WaitFor.True(() => model.Message is not null);

        model.Message.ShouldBe("Text is required.");
        closed.ShouldBeFalse();
    }

    [Fact]
    public void Cancel_closes_with_no_message()
    {
        var model = Model();
        string? closedWith = "not yet null";
        var raised = false;
        model.Closed += message =>
        {
            closedWith = message;
            raised = true;
        };

        model.Cancel.Execute(null);

        raised.ShouldBeTrue();
        closedWith.ShouldBeNull();
    }
}
