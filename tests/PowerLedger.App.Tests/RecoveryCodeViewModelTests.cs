using System.IO;
using PowerLedger.Contracts;
using Shouldly;

namespace PowerLedger.App.Tests;

public class RecoveryCodeViewModelTests
{
    private readonly FakeLink _link = new();

    private static HouseholdNotice Notice(string code = "K7QM-2XHD-9PW4-R8TA-VMNP-3QWE", string? promptId = "recovery-1")
        => new(NoticeKind.RecoveryCode, promptId, "Here's your recovery code.", null, null, null, code);

    [Fact]
    public void The_code_is_the_notices_own()
    {
        var model = new RecoveryCodeViewModel(_link, Notice(), new FakeSaver(), _ => { });

        model.Code.ShouldBe("K7QM-2XHD-9PW4-R8TA-VMNP-3QWE");
    }

    [Fact]
    public void Copy_copies_the_code_to_the_clipboard()
    {
        string? copied = null;
        var model = new RecoveryCodeViewModel(_link, Notice(), new FakeSaver(), text => copied = text);

        model.Copy.Execute(null);

        copied.ShouldBe("K7QM-2XHD-9PW4-R8TA-VMNP-3QWE");
    }

    [Fact]
    public void Save_as_text_file_writes_the_code_where_the_user_says()
    {
        using var saver = new FakeSaver();
        var model = new RecoveryCodeViewModel(_link, Notice(), saver, _ => { });

        model.SaveAsTextFile.Execute(null);

        saver.Suggested.ShouldBe("PowerLedger-recovery-code.txt");
        File.ReadAllText(saver.Chosen).ShouldBe("K7QM-2XHD-9PW4-R8TA-VMNP-3QWE");
    }

    [Fact]
    public void A_cancelled_save_writes_nothing()
    {
        using var saver = new FakeSaver { Cancel = true };
        var model = new RecoveryCodeViewModel(_link, Notice("THE-CODE"), saver, _ => { });

        model.SaveAsTextFile.Execute(null);

        File.Exists(saver.Chosen).ShouldBeFalse();
    }

    /// <summary>Review finding A6: a save that can't reach disk shows the problem and keeps the window (and the code)
    /// right where the user can still copy it, rather than throwing out of the command.</summary>
    [Fact]
    public void A_save_that_fails_shows_the_error_and_keeps_the_code()
    {
        using var saver = new FakeSaver { MissingFolder = true };
        var model = new RecoveryCodeViewModel(_link, Notice(), saver, _ => { });

        model.SaveAsTextFile.Execute(null);

        model.Message.ShouldNotBeNull();
        model.Code.ShouldBe("K7QM-2XHD-9PW4-R8TA-VMNP-3QWE");
    }

    /// <summary>Review finding A6: OK closes the window; the window's own Closed event is what answers the prompt (see
    /// the next test), the same as closing it any other way.</summary>
    [Fact]
    public void Ok_closes_the_window()
    {
        var model = new RecoveryCodeViewModel(_link, Notice(), new FakeSaver(), _ => { });
        var closed = 0;
        model.Closed += () => closed++;

        model.Ok.Execute(null);

        closed.ShouldBe(1);
    }

    [Fact]
    public void Answer_tells_the_service_it_can_stop_holding_the_code_and_only_the_first_call_counts()
    {
        _link.Connect(true);
        var model = new RecoveryCodeViewModel(_link, Notice(), new FakeSaver(), _ => { });

        model.Answer();
        model.Answer();   // the X button closing on top of an already-answered OK, say

        _link.HouseholdRequests.Single().ShouldBe(("recovery-1", true));
    }

    [Fact]
    public void With_no_promptid_answer_sends_nothing()
    {
        _link.Connect(true);
        var model = new RecoveryCodeViewModel(_link, Notice(promptId: null), new FakeSaver(), _ => { });

        model.Answer();

        _link.HouseholdRequests.ShouldBeEmpty();
    }
}
