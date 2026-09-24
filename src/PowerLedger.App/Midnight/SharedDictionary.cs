using System.Windows;

namespace PowerLedger.App;

/// <summary>
/// One instance of a dictionary per source on a thread. Styles.Midnight.xaml is merged by the window and by every
/// Midnight view: a view built by a DataTemplate resolves its StaticResources while it is still outside the window's
/// tree, so it has to carry the keys itself, and this keeps the window and the views reading one set of styles rather
/// than a copy each. The cache is per thread, since a dictionary belongs to the thread that loaded it.
/// </summary>
internal sealed class SharedDictionary : ResourceDictionary
{
    [ThreadStatic]
    private static Dictionary<Uri, ResourceDictionary>? _loaded;

    private Uri? _source;

    /// <summary>The absolute pack URI of the dictionary to share.</summary>
    public new Uri? Source
    {
        get => _source;
        set
        {
            _source = value;
            if (value is null) return;
            _loaded ??= [];
            if (!_loaded.TryGetValue(value, out var dictionary)) _loaded[value] = dictionary = new ResourceDictionary { Source = value };
            MergedDictionaries.Add(dictionary);
        }
    }
}
