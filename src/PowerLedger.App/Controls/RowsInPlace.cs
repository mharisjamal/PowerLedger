using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace PowerLedger.App;

/// <summary>
/// A list whose rows come new with every reading, drawn in place (perf review P14, as Plan U did for Aero's Dashboard): a
/// new list given to ItemsSource builds every row's elements again and lays the list out each second. Given to
/// <c>RowsInPlace.Items</c> instead, with the row's template as <c>RowsInPlace.Template</c>, each row keeps its elements
/// and only their bindings take the new row's values; a row equal to the one it replaces changes nothing. Rows are matched
/// by their place in the list; a longer or shorter list adds or takes away rows at the end.
/// </summary>
internal static class RowsInPlace
{
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.RegisterAttached(
        "Items", typeof(IEnumerable), typeof(RowsInPlace), new PropertyMetadata(null, OnItemsChanged));

    public static readonly DependencyProperty TemplateProperty = DependencyProperty.RegisterAttached(
        "Template", typeof(DataTemplate), typeof(RowsInPlace), new PropertyMetadata(null, OnTemplateChanged));

    private static readonly DependencyProperty SlotsProperty = DependencyProperty.RegisterAttached(
        "Slots", typeof(ObservableCollection<RowSlot>), typeof(RowsInPlace));

    public static IEnumerable? GetItems(DependencyObject control) => (IEnumerable?)control.GetValue(ItemsProperty);

    public static void SetItems(DependencyObject control, IEnumerable? value) => control.SetValue(ItemsProperty, value);

    public static DataTemplate? GetTemplate(DependencyObject control) => (DataTemplate?)control.GetValue(TemplateProperty);

    public static void SetTemplate(DependencyObject control, DataTemplate? value) => control.SetValue(TemplateProperty, value);

    private static void OnItemsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsControl control) return;
        if (control.GetValue(SlotsProperty) is not ObservableCollection<RowSlot> slots)
        {
            slots = [];
            control.SetValue(SlotsProperty, slots);
            control.ItemsSource = slots;
        }
        var rows = (e.NewValue as IEnumerable)?.Cast<object>().ToList() ?? [];
        for (var i = 0; i < rows.Count; i++)
        {
            if (i < slots.Count) slots[i].Row = rows[i];
            else slots.Add(new RowSlot(rows[i]));
        }
        while (slots.Count > rows.Count) slots.RemoveAt(slots.Count - 1);
    }

    /// <summary>Each row's elements: the row's own template in a presenter whose content is the slot's row. A presenter
    /// given its template keeps the elements it made from it as its content changes, so only their bindings move.</summary>
    private static void OnTemplateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsControl control) return;
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetBinding(ContentPresenter.ContentProperty, new Binding(nameof(RowSlot.Row)));
        presenter.SetValue(ContentPresenter.ContentTemplateProperty, e.NewValue);
        var wrapper = new DataTemplate { VisualTree = presenter };
        wrapper.Seal();
        control.ItemTemplate = wrapper;
    }

    /// <summary>One row's place in the list, holding whichever row is there now.</summary>
    private sealed class RowSlot(object row) : INotifyPropertyChanged
    {
        private object _row = row;

        public event PropertyChangedEventHandler? PropertyChanged;

        public object Row
        {
            get => _row;
            set
            {
                if (Equals(_row, value)) return;   // the rows are records: an unchanged one leaves its bindings be
                _row = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Row)));
            }
        }
    }
}
