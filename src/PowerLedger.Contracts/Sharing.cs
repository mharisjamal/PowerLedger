using System.Text.RegularExpressions;

namespace PowerLedger.Contracts;

/// <summary>The consent text's version (data-sharing design §1). Raise it when the wording of any switch changes
/// materially: consent given to an older wording then counts for nothing until the user answers again.</summary>
public static class ConsentText
{
    public const int Version = 1;
}

/// <summary>What the user agreed to send, for the whole machine (data-sharing design §1). <see cref="Share"/> needs
/// <see cref="Power"/>.</summary>
/// <param name="Version">The consent text's version the user answered; 0 when they never have.</param>
/// <param name="Diagnostics">Crash and sensor reports.</param>
/// <param name="Usage">Usage.</param>
/// <param name="Power">Hardware and power.</param>
/// <param name="Share">Share my detailed data: what <paramref name="Power"/> sends may be given or sold as it is.</param>
public sealed record Consent(int Version, bool Diagnostics, bool Usage, bool Power, bool Share)
{
    /// <summary>The state of a machine whose user has never answered: nothing may be sent.</summary>
    public static Consent Unanswered { get; } = new(0, false, false, false, false);

    /// <summary>True when the user answered the current wording, whatever they answered.</summary>
    public bool Answered => Version == ConsentText.Version;

    /// <summary>True when anything at all may be sent.</summary>
    public bool AllowsAny => Answered && (Diagnostics || Usage || Power);

    /// <summary>Null when this is an answer the service can record; otherwise why not, in words the App can show.</summary>
    public string? Validate() =>
        Version != ConsentText.Version ? "That answer is to an older wording of the choices. Please choose again."
        : Share && !Power ? "Sharing detailed data needs Hardware and power turned on."
        : null;
}

/// <summary>Where sharing stands, for Settings → Privacy.</summary>
/// <param name="Consent">What the user agreed to, as the service holds it.</param>
/// <param name="InstallId">The random ID uploads go under; null until a switch has been on.</param>
/// <param name="LastSentAt">When the server last accepted an upload.</param>
/// <param name="LastSentBytes">That upload's size as sent, compressed.</param>
/// <param name="Problem">Null while all is well; otherwise why the last try failed, in words the App can show.</param>
/// <param name="Rejected">True when <paramref name="Problem"/> is the server refusing a day, false when it wasn't reached.</param>
/// <param name="DaysWaiting">Complete days waiting to be sent.</param>
public sealed record SharingStatus(
    Consent Consent,
    string? InstallId,
    DateTimeOffset? LastSentAt,
    long? LastSentBytes,
    string? Problem,
    bool Rejected,
    int DaysWaiting);

/// <summary>What the App counted since its last report, sent while Usage is on. The counts add to what the service holds
/// for <paramref name="Day"/>; <paramref name="DaysSinceFirstRun"/>, <paramref name="Theme"/> and
/// <paramref name="Language"/> replace it.</summary>
/// <param name="Day">The local day the counts belong to, <c>yyyy-MM-dd</c>.</param>
/// <param name="AppOpens">Times the main window was opened.</param>
/// <param name="Pages">Times each page was opened, by a camelCase name such as <c>now</c> or <c>settings</c>.</param>
/// <param name="Settings">Times each setting was changed, by a camelCase name; never its value.</param>
/// <param name="ReportsExported">PDF reports written.</param>
/// <param name="UpdatesInstalled">Updates started from the App.</param>
/// <param name="DaysSinceFirstRun">Whole days since the App first finished setup.</param>
/// <param name="Theme"><c>light</c>, <c>dark</c> or <c>system</c>.</param>
/// <param name="Language">The display language, such as <c>en-US</c>.</param>
public sealed partial record UsageCounts(
    string Day,
    int AppOpens,
    IReadOnlyDictionary<string, int> Pages,
    IReadOnlyDictionary<string, int> Settings,
    int ReportsExported,
    int UpdatesInstalled,
    int DaysSinceFirstRun,
    string Theme,
    string Language)
{
    public const int MaxNames = 64;
    public const int MaxCount = 1_000_000;

    /// <summary>Null when the counts are acceptable; otherwise the first problem.</summary>
    public string? Validate()
    {
        if (Day is null || !DayPattern().IsMatch(Day)) return "The day must be yyyy-MM-dd.";
        if (Pages is null || Settings is null) return "The page and setting counts are missing.";
        if (Pages.Count > MaxNames || Settings.Count > MaxNames) return $"At most {MaxNames} pages and {MaxNames} settings can be counted.";
        foreach (var (name, count) in Pages.Concat(Settings))
        {
            if (name is null || !NamePattern().IsMatch(name)) return "A counted name must be camelCase letters and digits, at most 40.";
            if (count is < 0 or > MaxCount) return $"A count must be between 0 and {MaxCount}.";
        }
        if (AppOpens is < 0 or > MaxCount || ReportsExported is < 0 or > MaxCount || UpdatesInstalled is < 0 or > MaxCount)
            return $"A count must be between 0 and {MaxCount}.";
        if (DaysSinceFirstRun is < 0 or > 36500) return "Days since the first run must be between 0 and 36500.";
        if (Theme is not ("light" or "dark" or "system")) return "The theme must be light, dark or system.";
        if (Language is null || !LanguagePattern().IsMatch(Language)) return "The language must be a culture name such as en-US.";
        return null;
    }

    [GeneratedRegex("^[0-9]{4}-(0[1-9]|1[0-2])-(0[1-9]|[12][0-9]|3[01])$")]
    private static partial Regex DayPattern();

    [GeneratedRegex("^[a-z][A-Za-z0-9]{0,39}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex("^[a-z]{2,3}(-[A-Za-z0-9]{2,8}){0,3}$")]
    private static partial Regex LanguagePattern();
}

/// <summary>One crash, as it was caught, before scrubbing. The service scrubs names and paths out of it (data-sharing
/// design §3) before it is stored for sending.</summary>
/// <param name="At">When it happened.</param>
/// <param name="Component"><c>app</c> or <c>service</c>.</param>
/// <param name="Version">The version that crashed, <c>X.Y.Z</c>.</param>
/// <param name="Types">The exception's type, then each inner exception's, outermost first; 1 to 10 of them.</param>
/// <param name="Message">The outermost exception's message.</param>
/// <param name="Stack">The stack traces, outermost first.</param>
public sealed record CrashReport(
    DateTimeOffset At,
    string Component,
    string Version,
    IReadOnlyList<string> Types,
    string Message,
    string Stack)
{
    public const int MaxTypes = 10;
    public const int MaxTypeLength = 200;
    public const int MaxMessageLength = 2000;
    public const int MaxStackLength = 12000;

    /// <summary>A crash cut down to the lengths the pipe and the server accept, so a caught crash is never refused for its size.</summary>
    public CrashReport Trimmed() => this with
    {
        Types = (Types ?? []).Take(MaxTypes).Select(type => Cut(type ?? "", MaxTypeLength)).DefaultIfEmpty("System.Exception").ToArray(),
        Message = Cut(Message ?? "", MaxMessageLength),
        Stack = Cut(Stack ?? "", MaxStackLength),
    };

    /// <summary>Null when the report is acceptable; otherwise the first problem.</summary>
    public string? Validate()
    {
        if (Component is not ("app" or "service")) return "A crash's component must be app or service.";
        if (Version is null || Version.Length > 20) return "A crash's version is missing.";
        if (Types is not { Count: >= 1 and <= MaxTypes } || Types.Any(type => type is null || type.Length > MaxTypeLength))
            return $"A crash must name 1 to {MaxTypes} exception types of at most {MaxTypeLength} characters.";
        if (Message is null || Message.Length > MaxMessageLength) return $"A crash's message must be at most {MaxMessageLength} characters.";
        if (Stack is null || Stack.Length > MaxStackLength) return $"A crash's stack trace must be at most {MaxStackLength} characters.";
        return null;
    }

    private static string Cut(string text, int length) => text.Length <= length ? text : text[..length];
}
