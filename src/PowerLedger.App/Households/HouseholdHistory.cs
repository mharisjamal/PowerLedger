using System.Globalization;
using Microsoft.Data.Sqlite;
using PowerLedger.Storage;

namespace PowerLedger.App;

/// <summary>Today's, this week's and this month's household totals, and every member, from one set of reads so they
/// agree (households design §2).</summary>
internal sealed record HouseholdSnapshot(
    HouseholdRangeTotals Today, HouseholdRangeTotals Week, HouseholdRangeTotals Month, IReadOnlyList<HouseholdMemberRow> Members);

/// <summary>The Household page's read-only view of the database (Plan N task A1). Null when the database cannot be read.</summary>
internal interface IHouseholdHistory
{
    HouseholdSnapshot? Read(DateTimeOffset now, TimeZoneInfo zone);
}

/// <summary>Reads household_rows and household_members read-only, as <see cref="HistoryReader"/> reads the rest of
/// history (households design §9: the App never writes this table either).</summary>
internal sealed class HouseholdHistory(SqliteDatabase database) : IHouseholdHistory
{
    public HouseholdSnapshot? Read(DateTimeOffset now, TimeZoneInfo zone)
    {
        try
        {
            var queries = new HouseholdQueries(database);
            var today = Ranges.Today(now, zone, CultureInfo.InvariantCulture);
            var week = Ranges.ThisWeek(now, zone);
            var month = Ranges.ThisMonth(now, zone, CultureInfo.InvariantCulture);
            return new HouseholdSnapshot(
                queries.Totals(today.From, today.To),
                queries.Totals(week.From, week.To),
                queries.Totals(month.From, month.To),
                queries.Members());
        }
        catch (SqliteException)
        {
            return null;
        }
    }
}
