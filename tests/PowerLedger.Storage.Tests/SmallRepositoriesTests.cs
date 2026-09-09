using PowerLedger.Contracts;
using PowerLedger.Core;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Storage.Tests;

public class SmallRepositoriesTests
{
    [Fact]
    public void Sessions_open_close_and_list_by_overlap()
    {
        using var t = new TestDatabase();
        var repo = new SessionRepository(t.Db);
        var id = repo.Open(SessionReason.Boot, Fixtures.T0);
        repo.OpenSession()!.Id.ShouldBe(id);
        repo.OpenSession()!.Reason.ShouldBe(SessionReason.Boot);

        repo.Close(id, Fixtures.T0.AddHours(2), SessionReason.Suspend).ShouldBeTrue();
        repo.OpenSession().ShouldBeNull();
        var second = repo.Open(SessionReason.Resume, Fixtures.T0.AddHours(5));

        var overlapping = repo.List(Fixtures.T0.AddHours(1), Fixtures.T0.AddHours(6));
        overlapping.Select(s => s.Id).ShouldBe([id, second]);
        repo.List(Fixtures.T0.AddHours(2), Fixtures.T0.AddHours(5)).ShouldBeEmpty();
        overlapping[0].End.ShouldBe(Fixtures.T0.AddHours(2));
        overlapping[0].EndReason.ShouldBe(SessionReason.Suspend);
        overlapping[1].End.ShouldBeNull();
        overlapping[1].EndReason.ShouldBeNull();
    }

    [Fact]
    public void Dangling_sessions_from_a_crash_can_be_closed_in_one_call()
    {
        using var t = new TestDatabase();
        var repo = new SessionRepository(t.Db);
        repo.Open(SessionReason.ServiceStart, Fixtures.T0);
        repo.CloseAllOpen(Fixtures.T0.AddMinutes(10)).ShouldBe(1);
        repo.CloseAllOpen(Fixtures.T0.AddMinutes(11)).ShouldBe(0);
        var closed = repo.List(Fixtures.T0, Fixtures.T0.AddHours(1)).Single();
        closed.End.ShouldBe(Fixtures.T0.AddMinutes(10));
        closed.EndReason.ShouldBe(SessionReason.CrashRecovered);
    }

    [Fact]
    public void Tariffs_keep_six_decimals_and_come_back_sorted()
    {
        using var t = new TestDatabase();
        var repo = new TariffRepository(t.Db);
        repo.Add(new Tariff(Fixtures.T0.AddMonths(1), 0.20m, "USD"));
        repo.Add(new Tariff(Fixtures.T0, 0.123456m, "USD"));
        var all = repo.All();
        all.Select(x => x.PricePerKwh).ShouldBe([0.123456m, 0.20m]);
        all[0].Currency.ShouldBe("USD");
        repo.Schedule().At(Fixtures.T0.AddDays(1))!.PricePerKwh.ShouldBe(0.123456m);
    }

    [Fact]
    public void Settings_are_a_simple_key_value_store()
    {
        using var t = new TestDatabase();
        var repo = new SettingsRepository(t.Db);
        repo.Get("tariff.currency").ShouldBeNull();
        repo.Set("tariff.currency", "USD");
        repo.Set("tariff.currency", "EUR");
        repo.Get("tariff.currency").ShouldBe("EUR");
        repo.Set("idle.threshold", "300");
        repo.All().ShouldBe(new Dictionary<string, string> { ["tariff.currency"] = "EUR", ["idle.threshold"] = "300" });
    }

    [Fact]
    public void Sessions_come_back_in_start_order_and_the_newest_open_one_wins()
    {
        using var t = new TestDatabase();
        var repo = new SessionRepository(t.Db);
        var late = repo.Open(SessionReason.Resume, Fixtures.T0.AddHours(5));
        var early = repo.Open(SessionReason.Boot, Fixtures.T0);
        repo.List(Fixtures.T0, Fixtures.T0.AddHours(6)).Select(s => s.Id).ShouldBe([early, late]);
        repo.OpenSession()!.Id.ShouldBe(late);
        repo.CloseAllOpen(Fixtures.T0.AddHours(6)).ShouldBe(2);
        repo.Close(early, Fixtures.T0.AddHours(7), SessionReason.Shutdown).ShouldBeFalse();
        repo.Close(9999, Fixtures.T0.AddHours(7), SessionReason.Shutdown).ShouldBeFalse();
    }

    [Fact]
    public void Empty_tables_read_as_empty()
    {
        using var t = new TestDatabase();
        new TariffRepository(t.Db).All().ShouldBeEmpty();
        new TariffRepository(t.Db).Schedule().At(Fixtures.T0).ShouldBeNull();
        new SettingsRepository(t.Db).All().ShouldBeEmpty();
        new SessionRepository(t.Db).List(Fixtures.T0, Fixtures.T0.AddHours(1)).ShouldBeEmpty();
        new SessionRepository(t.Db).CloseAllOpen(Fixtures.T0).ShouldBe(0);
    }
}
