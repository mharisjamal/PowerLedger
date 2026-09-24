using System.IO;
using Shouldly;

namespace PowerLedger.App.Tests;

public class FeedbackLogTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pl-feedback-log-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void With_no_matching_file_there_is_nothing_to_tail()
    {
        Directory.CreateDirectory(_folder);
        FeedbackLog.TailLatestFile(_folder, "app-*.log").ShouldBeNull();
    }

    [Fact]
    public void A_missing_folder_is_read_as_nothing_rather_than_thrown()
        => FeedbackLog.TailLatestFile(Path.Combine(_folder, "missing"), "app-*.log").ShouldBeNull();

    [Fact]
    public void Only_the_newest_matching_file_is_tailed()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "app-20260101.log"), "old");
        var newest = Path.Combine(_folder, "app-20260908.log");
        File.WriteAllText(newest, "new");
        File.SetLastWriteTimeUtc(newest, DateTime.UtcNow.AddMinutes(1));

        FeedbackLog.TailLatestFile(_folder, "app-*.log").ShouldBe("new");
    }

    [Fact]
    public void Only_the_last_lines_are_kept()
    {
        var lines = Enumerable.Range(1, 500).Select(n => $"line {n}").ToArray();

        var tail = FeedbackLog.TailLines_(lines, 300);

        tail.Split('\n').Length.ShouldBe(300);
        tail.Split('\n')[0].ShouldBe("line 201");
        tail.Split('\n')[^1].ShouldBe("line 500");
    }

    [Fact]
    public void Combined_is_null_with_neither_log()
        => FeedbackLog.Combined(null, null).ShouldBeNull();

    [Fact]
    public void Combined_heads_each_log_and_joins_them()
    {
        var combined = FeedbackLog.Combined("app line", "service line")!;

        combined.ShouldContain("--- App log ---\napp line");
        combined.ShouldContain("--- Service log ---\nservice line");
    }

    [Fact]
    public void Combined_is_cut_to_the_workers_own_character_limit_keeping_the_most_recent()
    {
        var big = new string('a', FeedbackLog.MaxChars + 500);

        var combined = FeedbackLog.Combined(big, null)!;

        combined.Length.ShouldBe(FeedbackLog.MaxChars);
        combined.ShouldBe(big[^FeedbackLog.MaxChars..]);
    }
}
