using System.Windows;
using System.Windows.Controls;

namespace PowerLedger.App.Midnight;

/// <summary>The range pills and custom dates Midnight's History and Report pages share; its data context is a <see cref="RangePicker"/>.</summary>
public partial class RangePills : UserControl
{
    /// <summary>Whether the Dashboard's last hour and last year join the rolling ranges. History offers them, since
    /// BreakdownViewModel's range resolves both as it is; the Report keeps Classic's six.</summary>
    public static readonly DependencyProperty ShowHourAndYearProperty =
        DependencyProperty.Register(nameof(ShowHourAndYear), typeof(bool), typeof(RangePills), new PropertyMetadata(false));

    public RangePills() => InitializeComponent();

    public bool ShowHourAndYear
    {
        get => (bool)GetValue(ShowHourAndYearProperty);
        set => SetValue(ShowHourAndYearProperty, value);
    }
}
