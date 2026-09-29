using System.Globalization;
using PowerLedger.Contracts;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>Settings' UPS on another computer (Network UPS Tools): the server's boxes and the password that goes one way.</summary>
public class ServiceFormNutTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static readonly NutSettings Nas = new() { Host = "nas.local", Ups = "myups", Username = "monitor", HasPassword = true };
    private readonly FakeLink _link = new();

    public ServiceFormNutTests() => _link.Connect(true);

    [Fact]
    public void Loading_fills_the_server_s_boxes_and_says_a_password_is_held_without_showing_it()
    {
        var form = Form(ServiceSettings.Default with { Nut = Nas with { Port = 3500 } });

        (form.NutHost, form.NutPort, form.NutUps, form.NutUser).ShouldBe(("nas.local", "3500", "myups", "monitor"));
        form.NutHasPassword.ShouldBeTrue();
        form.NutPassword.ShouldBeNull();
    }

    [Fact]
    public void A_service_from_before_the_ups_server_leaves_the_boxes_empty_and_the_port_at_its_default()
    {
        var form = Form(ServiceSettings.Default with { Nut = null! });

        (form.NutHost, form.NutPort, form.NutUps, form.NutUser).ShouldBe(("", "3493", "", ""));
    }

    [Fact]
    public async Task A_server_typed_is_sent_whole_and_the_password_only_when_one_was_typed()
    {
        var form = Form(ServiceSettings.Default);
        form.NutHost = " nas.local ";
        form.NutUps = "myups";
        form.NutUser = "monitor";

        (await form.SaveAsync()).ShouldBeTrue();
        Sent().Nut.ShouldBe(new NutSettings { Host = "nas.local", Ups = "myups", Username = "monitor" });

        form.NutPassword = "sec ret";
        (await form.SaveAsync()).ShouldBeTrue();
        Sent().Nut.Password.ShouldBe("sec ret");
        form.NutHasPassword.ShouldBeTrue();

        (await form.SaveAsync()).ShouldBeTrue();
        Sent().Nut.Password.ShouldBeNull();                 // sent once, then let go of: the service keeps it
        Sent().Nut.HasPassword.ShouldBeTrue();
    }

    [Fact]
    public async Task An_emptied_password_forgets_the_one_held()
    {
        var form = Form(ServiceSettings.Default with { Nut = Nas });

        form.NutPassword = "";
        (await form.SaveAsync()).ShouldBeTrue();

        Sent().Nut.Password.ShouldBe("");
        form.NutHasPassword.ShouldBeFalse();
    }

    [Fact]
    public async Task A_form_that_saves_itself_sends_a_typed_password_at_once()
    {
        var form = new ServiceForm(_link, UiThreads.Inline, English, savesItself: true);
        form.Load(ServiceSettings.Default with { Nut = Nas });

        form.NutPassword = "sec ret";

        await WaitFor(() => _link.Writes.OfType<ServiceSettings>().Any());
        Sent().Nut.Password.ShouldBe("sec ret");
    }

    [Theory]
    [InlineData("nas.local", "", "Type both the UPS server and the UPS's name, or neither.")]
    [InlineData("nas local", "myups", "Type the UPS server as a computer name or an IP address.")]
    public async Task A_server_the_service_would_refuse_is_named_and_nothing_is_sent(string host, string ups, string problem)
    {
        var form = Form(ServiceSettings.Default);
        form.NutHost = host;
        form.NutUps = ups;

        (await form.SaveAsync()).ShouldBeFalse();

        form.Message.ShouldBe(problem);
        _link.Writes.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_port_that_isn_t_a_number_is_named()
    {
        var form = Form(ServiceSettings.Default with { Nut = Nas });
        form.NutPort = "abc";

        (await form.SaveAsync()).ShouldBeFalse();

        form.Message.ShouldBe("Type the UPS server's port as a whole number.");
    }

    [Fact]
    public void What_the_service_says_of_the_server_shows_only_while_one_is_set_up()
    {
        var form = Form(ServiceSettings.Default with { Nut = Nas });
        SourceStatus[] sources = [new("nut", true, "nas.local can't be reached: nothing answered at that address", 0, null)];

        form.ShowNut(sources);
        form.NutSaid.ShouldBe("nas.local can't be reached: nothing answered at that address");

        form.ShowNut([new SourceStatus("nut", true, null, 0, null)]);
        form.NutSaid.ShouldBeNull();

        var off = Form(ServiceSettings.Default);
        off.ShowNut(sources);
        off.NutSaid.ShouldBeNull();
    }

    private ServiceForm Form(ServiceSettings settings)
    {
        var form = new ServiceForm(_link, UiThreads.Inline, English);
        form.Load(settings);
        return form;
    }

    private ServiceSettings Sent() => _link.Writes.OfType<ServiceSettings>().Last();

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
        condition().ShouldBeTrue();
    }
}
