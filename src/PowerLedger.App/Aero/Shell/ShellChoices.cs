using System.Globalization;

namespace PowerLedger.App;

/// <summary>
/// Aero's Switch look (Aero look design §2) and its banner's Switch back (§1): the sidebar's item offers the other two
/// looks, each chosen through <see cref="SettingsViewModel.Look"/> so it is saved; Switch back goes to the look the move
/// to Aero left (<see cref="UiPreferences.LookBeforeAero"/>), Midnight when it left none.
/// </summary>
internal static class AeroLooks
{
    /// <summary>The looks Switch look offers, Midnight first as the nearer of the two.</summary>
    public static IReadOnlyList<Look> Others { get; } = [Look.Midnight, Look.Classic];

    /// <summary>A few words under each look's name in the menu.</summary>
    public static string Describe(Look look) => look switch
    {
        Look.Midnight => "Dark and quiet, without the glass",
        Look.Classic => "The original look",
        _ => "Liquid glass",
    };

    /// <summary>Where Switch back goes: the look before Aero, Midnight when there was none (or it was Aero already).</summary>
    public static Look SwitchBackTo(Look? before) => before is Look.Classic or Look.Midnight ? before.Value : Look.Midnight;
}

/// <summary>
/// The top bar's bell (Aero look design §1): PCs waiting for approval, and today's unusual hours from Insights, three
/// at the most, newest first (§4), each worded for the menu.
/// </summary>
internal static class Bell
{
    /// <summary>"2:00 PM, 3.5 times the usual".</summary>
    public static string Line(UsageAnomaly alert, TimeZoneInfo zone, CultureInfo culture)
        => $"{TimeZoneInfo.ConvertTime(alert.Hour, zone).ToString(culture.DateTimeFormat.ShortTimePattern, culture)}, "
            + $"{Math.Round(alert.Times, 1).ToString("0.#", culture)} times the usual";

    /// <summary>The hour's energy, at the right of its line: "0.420 kWh".</summary>
    public static string Figure(UsageAnomaly alert, CultureInfo culture) => Format.Kwh(alert.Kwh, culture) + " kWh";

    /// <summary>The approvals line: nothing waiting, or how many PCs wait to join.</summary>
    public static string Approvals(int waiting) => waiting switch
    {
        <= 0 => "Nothing is waiting for you.",
        1 => "1 PC is waiting to join",
        _ => $"{waiting.ToString(CultureInfo.CurrentCulture)} PCs are waiting to join",
    };

    /// <summary>Whether the bell shows its dot: something waits in it.</summary>
    public static bool HasNews(int approvals, int alerts) => approvals > 0 || alerts > 0;
}

/// <summary>One of the sidebar's "Your PCs": its initials, its name, and its figure (this PC's watts now, another's energy
/// this month), and its share of the household's month and that energy ("34.2 kWh"), for This month's split.</summary>
internal sealed record PcRow(string Initials, string Name, string Figure, bool IsThisPc, double Share, string Energy);

/// <summary>
/// The sidebar's "Your PCs" and the household button (Aero look design §1), from <see cref="HouseholdViewModel.Members"/>
/// and the live reading: this PC first with its watts now, the others with this month's energy, as the household's rows
/// have no live figure for another PC; a PC that has left is left out. Without a household, this PC alone.
/// </summary>
internal static class YourPcs
{
    public static IReadOnlyList<PcRow> Rows(IReadOnlyList<HouseholdMemberDisplay> members, double wattsNow, CultureInfo culture)
    {
        var live = double.IsFinite(wattsNow) ? Format.WholeWatts(wattsNow, culture) + " W" : Format.NoReading;
        var current = members.Where(m => !m.IsLeft).ToList();
        if (current.FirstOrDefault(m => m.IsThisPc) is not { } me)
            return [new PcRow(Initials.Letters(Environment.MachineName), "This PC", live, true, 1, Format.Missing), .. Others(current)];
        return [new PcRow(Initials.Letters(me.Name), "This PC", live, true, me.Share, me.Energy + " kWh"), .. Others(current)];
    }

    private static IEnumerable<PcRow> Others(IEnumerable<HouseholdMemberDisplay> members)
        => members.Where(m => !m.IsThisPc).Select(m => new PcRow(Initials.Letters(m.Name), m.Name, m.Energy + " kWh", false, m.Share, m.Energy + " kWh"));

    /// <summary>The household button's second line.</summary>
    public static string Summary(int pcs) => pcs > 1 ? $"{pcs.ToString(CultureInfo.CurrentCulture)} PCs in this household" : "This PC only";
}
