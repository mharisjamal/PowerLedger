using System.Windows.Controls;

namespace PowerLedger.App;

/// <summary>The Now screen's layout (spec §9); everything it shows comes from <see cref="NowViewModel"/>.</summary>
public partial class NowView : UserControl
{
    public NowView() => InitializeComponent();
}
