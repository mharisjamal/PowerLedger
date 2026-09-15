using System.Windows;
using System.Windows.Controls;
using PowerLedger.Contracts;

namespace PowerLedger.App;

/// <summary>The quality label (spec §9), templated in Styles.xaml; hidden while there is no reading.</summary>
internal sealed class QualityBadge : Control
{
    public static readonly DependencyProperty QualityProperty = DependencyProperty.Register(
        nameof(Quality), typeof(Quality?), typeof(QualityBadge), new PropertyMetadata(null));

    public Quality? Quality { get => (Quality?)GetValue(QualityProperty); set => SetValue(QualityProperty, value); }
}
