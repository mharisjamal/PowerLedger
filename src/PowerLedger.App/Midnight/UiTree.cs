using System.Windows;
using System.Windows.Media;

namespace PowerLedger.App;

/// <summary>A walk over the visual tree, for the shell to find its own parts under a panel.</summary>
internal static class UiTree
{
    /// <summary>Every <typeparamref name="T"/> under <paramref name="root"/>, outermost first.</summary>
    public static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }
}
