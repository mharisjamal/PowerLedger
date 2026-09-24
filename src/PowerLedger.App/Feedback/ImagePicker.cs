using Microsoft.Win32;

namespace PowerLedger.App;

/// <summary>Asks which image files to attach to feedback.</summary>
internal interface IImagePicker
{
    IReadOnlyList<string> Ask();
}

/// <summary>Windows' own Open dialog, filtered to PNG and JPEG, more than one at a time.</summary>
internal sealed class ImagePicker : IImagePicker
{
    public IReadOnlyList<string> Ask()
    {
        var dialog = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg", Multiselect = true, CheckFileExists = true };
        return dialog.ShowDialog() == true ? dialog.FileNames : [];
    }
}
