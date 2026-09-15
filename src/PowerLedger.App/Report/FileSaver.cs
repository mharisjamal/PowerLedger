using Microsoft.Win32;

namespace PowerLedger.App;

/// <summary>Asks where to save a file.</summary>
internal interface IFileSaver
{
    /// <summary>The path chosen for a file suggested as <paramref name="name"/>, or null when the user cancels.</summary>
    string? Ask(string name, string filter);
}

/// <summary>Windows' Save dialog, opening in Documents.</summary>
internal sealed class FileSaver : IFileSaver
{
    public string? Ask(string name, string filter)
    {
        var dialog = new SaveFileDialog
        {
            FileName = name,
            Filter = filter,
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
