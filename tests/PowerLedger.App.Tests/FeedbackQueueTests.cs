using System.IO;
using Shouldly;

namespace PowerLedger.App.Tests;

public class FeedbackQueueTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 14, 32, 0, TimeSpan.Zero);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pl-feedback-queue-tests", Guid.NewGuid().ToString("n"));

    private static FeedbackReport Report(string text = "It broke") => new(text, null, "0.7.0+abc1234", "Windows 11 Home 26200", "x64", null, []);

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void A_saved_report_reads_back_with_when_it_was_saved()
    {
        FeedbackQueue.Save(_folder, Report(), Now);

        var all = FeedbackQueue.ReadAll(_folder);

        all.Count.ShouldBe(1);
        all[0].Pending.SavedAt.ShouldBe(Now);
        all[0].Pending.Report.Text.ShouldBe("It broke");
    }

    [Fact]
    public void Reading_an_empty_or_missing_folder_finds_nothing()
        => FeedbackQueue.ReadAll(Path.Combine(_folder, "missing")).ShouldBeEmpty();

    [Fact]
    public void Reports_read_back_oldest_first()
    {
        FeedbackQueue.Save(_folder, Report("second"), Now.AddMinutes(5));
        FeedbackQueue.Save(_folder, Report("first"), Now);

        var all = FeedbackQueue.ReadAll(_folder);

        all.Select(f => f.Pending.Report.Text).ShouldBe(["first", "second"]);
    }

    [Fact]
    public void A_corrupt_file_is_skipped_rather_than_thrown()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "feedback-1.json"), "{ not json");
        FeedbackQueue.Save(_folder, Report(), Now);

        FeedbackQueue.ReadAll(_folder).Count.ShouldBe(1);
    }

    [Fact]
    public void Delete_removes_the_file_the_path_names()
    {
        FeedbackQueue.Save(_folder, Report(), Now);
        var path = FeedbackQueue.ReadAll(_folder).Single().Path;

        FeedbackQueue.Delete(path);

        FeedbackQueue.ReadAll(_folder).ShouldBeEmpty();
    }
}
