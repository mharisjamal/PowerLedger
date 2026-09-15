using System.Windows.Controls;

namespace PowerLedger.App;

/// <summary>The range buttons and custom dates both history screens share; its data context is a <see cref="RangePicker"/>.</summary>
public partial class RangeBar : UserControl
{
    public RangeBar() => InitializeComponent();
}
