using CommunityToolkit.Mvvm.ComponentModel;

namespace PowerLedger.App;

/// <summary>
/// TEMPORARY (plan O M1): a stand-in for F8's DashboardViewModel with only what the shell needs, so the Midnight window
/// compiles and renders before plan-o/f lands the real one; replaced at the merge.
/// </summary>
internal sealed class DashboardViewModel : ObservableObject
{
    public DashboardViewModel(NowViewModel now)
    {
        Now = now;
    }

    public NowViewModel Now { get; }

    public void Show()
    {
    }

    public void Hide()
    {
    }
}
