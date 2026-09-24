using CommunityToolkit.Mvvm.Input;
using Shouldly;

namespace PowerLedger.App.Tests;

public class WhatsNewViewModelTests
{
    [Fact]
    public void Title_and_points_pass_through_as_given()
    {
        var model = new WhatsNewViewModel("What's new in 0.7.0", ["One thing.", "Another thing."], new RelayCommand(() => { }));

        model.Title.ShouldBe("What's new in 0.7.0");
        model.Points.ShouldBe(["One thing.", "Another thing."]);
    }

    [Fact]
    public void Open_full_notes_is_the_command_it_was_given()
    {
        var opened = 0;
        var openFullNotes = new RelayCommand(() => opened++);
        var model = new WhatsNewViewModel("What's new in 0.7.0", [], openFullNotes);

        model.OpenFullNotes.Execute(null);

        opened.ShouldBe(1);
    }

    [Fact]
    public void Close_raises_closed()
    {
        var model = new WhatsNewViewModel("What's new in 0.7.0", [], new RelayCommand(() => { }));
        var closed = false;
        model.Closed += () => closed = true;

        model.Close.Execute(null);

        closed.ShouldBeTrue();
    }
}
