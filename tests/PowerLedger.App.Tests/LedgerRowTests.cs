using System.Windows;
using System.Windows.Controls;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>
/// A ledger line's unit is fixed text beside its value ("kWh", "W while on"), so a line with no value would read
/// "N/A kWh": in either look the note goes when the value is <see cref="Format.Missing"/>, and stays otherwise.
/// </summary>
[Trait("Category", "UI")]
public class LedgerRowTests
{
    [Theory]
    [InlineData("Classic")]
    [InlineData("Midnight")]
    public void A_missing_value_takes_its_unit_away_with_it(string look)
    {
        UiHarness.OnUi(() =>
        {
            Note(look, Format.Missing, "kWh").Visibility.ShouldBe(Visibility.Collapsed, "N/A with no unit after it");
            Note(look, Format.Missing, "W while on").Visibility.ShouldBe(Visibility.Collapsed);
            Note(look, "1.24", "kWh").Visibility.ShouldBe(Visibility.Visible, "a figure keeps its unit");
        });
    }

    /// <summary>The note's TextBlock of a line laid out in <paramref name="look"/>'s template.</summary>
    private static TextBlock Note(string look, string value, string note)
    {
        var row = new LedgerRow { Key = "Energy", Value = value, Note = note };
        var host = new Border { Child = row };
        if (look == "Midnight")
        {
            var styles = new ResourceDictionary { Source = new Uri("pack://application:,,,/PowerLedger;component/Midnight/Styles.Midnight.Pages.xaml") };
            host.Resources.MergedDictionaries.Add(styles);
            row.Style = (Style)styles["M.LedgerRow"];
        }
        host.Measure(new Size(400, 100));
        host.Arrange(new Rect(0, 0, 400, 100));
        row.ApplyTemplate();
        return (TextBlock)row.Template.FindName("Note", row);
    }
}
