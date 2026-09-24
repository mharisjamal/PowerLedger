using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

namespace PowerLedger.App;

/// <summary>What's new (owner's round): the running version's own points, plain, in-app; "Full notes on GitHub" still
/// opens the browser, the same page <see cref="Updater.OpenNotes"/> always has.</summary>
internal sealed class WhatsNewViewModel
{
    public WhatsNewViewModel(string title, IReadOnlyList<string> points, ICommand openFullNotes)
    {
        Title = title;
        Points = points;
        OpenFullNotes = openFullNotes;
        Close = new RelayCommand(() => Closed?.Invoke());
    }

    public string Title { get; }

    public IReadOnlyList<string> Points { get; }

    public ICommand OpenFullNotes { get; }

    public ICommand Close { get; }

    /// <summary>Close was pressed: the window closes.</summary>
    public event Action? Closed;
}
