using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using PowerLedger.Contracts;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>
/// Perf review P14: Midnight's parts, Classic's budget and Aero's Parts page were handed a new list with every reading,
/// which built every row's elements again and laid the list out each second. Their rows now keep their elements, and
/// only the bindings take the new values.
/// </summary>
[Trait("Category", "UI")]
public class RowsInPlaceTests
{
    private sealed record Row(string Name, string Watts);

    private static DataTemplate Template() => (DataTemplate)XamlReader.Parse(
        """
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
            <StackPanel Orientation="Horizontal">
                <TextBlock Text="{Binding Name}" />
                <TextBlock Text="{Binding Watts}" />
            </StackPanel>
        </DataTemplate>
        """);

    private static List<TextBlock> Cells(ItemsControl list)
    {
        list.UpdateLayout();
        return [.. Enumerable.Range(0, list.Items.Count)
            .Select(i => (ContentPresenter)list.ItemContainerGenerator.ContainerFromIndex(i))
            .SelectMany(container => UiHarness.Find<StackPanel>(container)!.Children.OfType<TextBlock>())];
    }

    [Fact]
    public void A_new_list_of_rows_changes_the_rows_in_place()
        => UiHarness.OnUi(() =>
        {
            var list = new ItemsControl();
            RowsInPlace.SetTemplate(list, Template());
            var window = new Window
            {
                Content = list, Width = 300, Height = 200, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0,
            };
            window.Show();
            try
            {
                RowsInPlace.SetItems(list, new[] { new Row("CPU", "14.6 W"), new Row("GPU", "4.1 W") });
                var before = Cells(list);
                before.Select(cell => cell.Text).ShouldBe(["CPU", "14.6 W", "GPU", "4.1 W"]);

                RowsInPlace.SetItems(list, new[] { new Row("CPU", "15.2 W"), new Row("GPU", "4.1 W") });
                var after = Cells(list);

                after.Select(cell => cell.Text).ShouldBe(["CPU", "15.2 W", "GPU", "4.1 W"]);
                after.Zip(before).ShouldAllBe(pair => ReferenceEquals(pair.First, pair.Second), "the same elements, given new values");

                RowsInPlace.SetItems(list, new[] { new Row("CPU", "15.2 W"), new Row("GPU", "4.1 W"), new Row("Display", "4.0 W") });
                Cells(list).Select(cell => cell.Text).ShouldBe(["CPU", "15.2 W", "GPU", "4.1 W", "Display", "4.0 W"]);
                Cells(list).Take(4).Zip(before).ShouldAllBe(pair => ReferenceEquals(pair.First, pair.Second), "a row added at the end leaves the others be");

                RowsInPlace.SetItems(list, new[] { new Row("CPU", "9.9 W") });
                Cells(list).Select(cell => cell.Text).ShouldBe(["CPU", "9.9 W"]);
                RowsInPlace.SetItems(list, null);
                list.Items.Count.ShouldBe(0);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public void Midnights_parts_table_keeps_its_rows_as_readings_come()
        => UiHarness.OnUi(() =>
        {
            using var saver = new FakeSaver();
            var now = MidnightFixtures.NowScreen(out var link);
            var shell = new ShellViewModel(now, MidnightFixtures.BreakdownScreen(), MidnightFixtures.ReportScreen(saver), MidnightFixtures.HouseholdScreen(),
                MidnightFixtures.SettingsScreen(), MidnightFixtures.WizardScreen(), "0.8.0", null, MidnightFixtures.DashboardScreen(now))
            {
                Page = Page.Dashboard,
            };
            var window = MidnightFixtures.Window(shell);
            window.Show();
            try
            {
                UiHarness.Pump(TimeSpan.FromMilliseconds(300));
                var view = UiHarness.Find<DashboardView>(window)!;
                var table = UiHarness.Find<ItemsControl>(view, list => BindingOperations.GetBindingExpression(list, RowsInPlace.ItemsProperty) is not null)!;
                var cpuNow = shell.Dashboard!.Parts.Single(part => part.Part == Part.Cpu).NowW;
                var cell = UiHarness.Find<TextBlock>(table, text => text.Text == cpuNow).ShouldNotBeNull("the CPU's watts now");

                link.Push(Frames.At(MidnightFixtures.Now.AddSeconds(1), cpu: 17.3));
                table.UpdateLayout();

                cell.Text.ShouldBe("17.3 W", "the same element, given the new value");
                table.Items.Count.ShouldBe(4);
            }
            finally
            {
                window.CloseForSwitch();
            }
        });
}
