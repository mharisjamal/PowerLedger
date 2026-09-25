using System.Text.Json;
using Json.Schema;
using PowerLedger.Contracts;
using Shouldly;

namespace PowerLedger.Service.Tests;

/// <summary>The contract the data-sharing pieces meet (data-sharing design §9): the new pipe messages, the consent and
/// count rules, and the report schema the Worker shares, checked against the fixtures the Worker's own tests use.</summary>
public class SharingContractTests
{
    public static TheoryData<string> Named(string prefix)
    {
        var names = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(ReportSchema.Contract, "fixtures"), prefix + "*.json").Order())
            names.Add(Path.GetFileName(file));
        return names;
    }

    private static EvaluationResults Evaluate(string fixture)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(ReportSchema.Contract, "fixtures", fixture)));
        return ReportSchema.Evaluate(document.RootElement);
    }

    private static string Problems(EvaluationResults results) => ReportSchema.Problems(results);

    [Theory]
    [MemberData(nameof(Named), "valid-")]
    public void AValidFixturePassesTheSchema(string fixture)
    {
        var results = Evaluate(fixture);
        results.IsValid.ShouldBeTrue(Problems(results));
    }

    [Theory]
    [MemberData(nameof(Named), "invalid-schema-")]
    public void AFixtureTheSchemaRefusesFailsIt(string fixture) => Evaluate(fixture).IsValid.ShouldBeFalse();

    // The minutes' columns are left to the Worker's own check, which keeps a report inside its CPU limit, so these pass
    // the schema and must be refused by that check instead (server/test).
    [Theory]
    [MemberData(nameof(Named), "invalid-minutes-")]
    public void AFixtureWithBadMinutesIsLeftToTheServersOwnCheck(string fixture)
    {
        var results = Evaluate(fixture);
        results.IsValid.ShouldBeTrue(Problems(results));
    }

    [Fact]
    public void TheNewMessagesRoundTripThroughThePipe()
    {
        PipeMessage[] messages =
        [
            new SetConsentRequest(1, new Consent(ConsentText.Version, true, false, true, true)),
            new ReportUsageRequest(2, new UsageCounts("2026-09-24", 1, new Dictionary<string, int> { ["now"] = 2 },
                new Dictionary<string, int> { ["theme"] = 1 }, 0, 0, 3, "dark", "en-US")),
            new ReportCrashRequest(3, new CrashReport(new DateTimeOffset(2026, 9, 24, 10, 0, 0, TimeSpan.Zero), "app", "0.6.0",
                ["System.InvalidOperationException"], "boom", "   at X()")),
            new PreviewUploadRequest(4),
            new SendNowRequest(5),
            new DeleteMyDataRequest(6),
            new SharingReply(7, true, "Written.", @"C:\ProgramData\PowerLedger\Sent\preview.json"),
        ];
        foreach (var message in messages)
        {
            var line = PipeProtocol.Serialize(message);
            var back = PipeProtocol.Deserialize(line.AsSpan(0, line.Length - 1));
            back.GetType().ShouldBe(message.GetType());
            // Lists and dictionaries compare by reference in records, so the line itself is compared.
            PipeProtocol.Serialize(back).ShouldBe(line);
        }
    }

    [Fact]
    public void AStatusFromAnOlderServiceHasNoSharing()
    {
        var status = new ServiceStatus("0.5.0", DateTimeOffset.UnixEpoch, 0, [], 0, 0, new CalibrationStatus(0, 0, 0, 0), "",
            0, null, null, null);
        var line = PipeProtocol.Serialize(new StatusReply(1, status));
        var back = PipeProtocol.Deserialize(line.AsSpan(0, line.Length - 1)).ShouldBeOfType<StatusReply>();
        back.Status.Sharing.ShouldBeNull();
    }

    [Fact]
    public void ConsentToTheCurrentWordingWithSharingAndPowerIsRecordable()
    {
        new Consent(ConsentText.Version, false, false, true, true).Validate().ShouldBeNull();
        new Consent(ConsentText.Version, false, false, false, false).Validate().ShouldBeNull();
    }

    [Fact]
    public void SharingWithoutHardwareAndPowerIsRefused() =>
        new Consent(ConsentText.Version, true, true, false, true).Validate().ShouldNotBeNull();

    [Fact]
    public void AnAnswerToAWordingNoLongerStandingIsRefused()
    {
        new Consent(ConsentText.Oldest - 1, true, false, false, false).Validate().ShouldNotBeNull();
        new Consent(ConsentText.Version + 1, true, false, false, false).Validate().ShouldNotBeNull();
    }

    [Fact]
    public void TheWordingIsVersionTwoAndAnAnswerToVersionOneStillStands()
    {
        (ConsentText.Oldest, ConsentText.Version).ShouldBe((1, 2));
        foreach (var version in new[] { 1, 2 })
        {
            var consent = new Consent(version, true, true, true, true);
            consent.Validate().ShouldBeNull();
            consent.Answered.ShouldBeTrue();
            consent.AllowsAny.ShouldBeTrue();
        }
    }

    [Fact]
    public void NothingMayBeSentUntilAStandingWordingIsAnsweredWithASwitchOn()
    {
        Consent.Unanswered.AllowsAny.ShouldBeFalse();
        Consent.Unanswered.Answered.ShouldBeFalse();
        new Consent(ConsentText.Version, false, false, false, false).AllowsAny.ShouldBeFalse();
        new Consent(ConsentText.Version, false, true, false, false).AllowsAny.ShouldBeTrue();
        new Consent(ConsentText.Version + 1, true, true, true, false).AllowsAny.ShouldBeFalse();
    }

    private static UsageCounts Counts() => new("2026-09-24", 3, new Dictionary<string, int> { ["now"] = 5 },
        new Dictionary<string, int> { ["tariff"] = 1 }, 0, 0, 12, "system", "en-US");

    [Fact]
    public void GoodCountsAreAccepted() => Counts().Validate().ShouldBeNull();

    [Theory]
    [InlineData("24-09-2026")]
    [InlineData("2026-13-01")]
    public void CountsForABadDayAreRefused(string day) => (Counts() with { Day = day }).Validate().ShouldNotBeNull();

    [Theory]
    [InlineData("Now")]
    [InlineData("now page")]
    [InlineData("")]
    public void ACountedNameThatIsntCamelCaseIsRefused(string name) =>
        (Counts() with { Pages = new Dictionary<string, int> { [name] = 1 } }).Validate().ShouldNotBeNull();

    [Fact]
    public void ANegativeCountIsRefused() =>
        (Counts() with { Settings = new Dictionary<string, int> { ["theme"] = -1 } }).Validate().ShouldNotBeNull();

    [Fact]
    public void AnUnknownThemeIsRefused() => (Counts() with { Theme = "blue" }).Validate().ShouldNotBeNull();

    [Fact]
    public void AnOversizedCrashIsTrimmedToWhatIsAccepted()
    {
        var crash = new CrashReport(DateTimeOffset.UnixEpoch, "app", "0.6.0",
            Enumerable.Repeat(new string('T', 500), 15).ToArray(), new string('m', 5000), new string('s', 20000));
        crash.Validate().ShouldNotBeNull();
        var trimmed = crash.Trimmed();
        trimmed.Validate().ShouldBeNull();
        trimmed.Types.Count.ShouldBe(CrashReport.MaxTypes);
        trimmed.Message.Length.ShouldBe(CrashReport.MaxMessageLength);
        trimmed.Stack.Length.ShouldBe(CrashReport.MaxStackLength);
    }

    [Fact]
    public void ACrashFromSomethingElseIsRefused() =>
        new CrashReport(DateTimeOffset.UnixEpoch, "installer", "0.6.0", ["X"], "", "").Validate().ShouldNotBeNull();
}
