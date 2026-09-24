using System.IO;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

namespace PowerLedger.App;

/// <summary>
/// A recovery code, shown once (households design §7, Plan N task A7): made the first time an account links a
/// household, and carried back in that <see cref="HouseholdReply"/>'s Code. Copy and Save as text file are the only way
/// to keep it, since the App never stores it itself.
/// </summary>
internal sealed class RecoveryCodeViewModel
{
    public const string Explanation =
        "Keep this code safe. With it and your account you can get your household back if you lose every PC.";

    public RecoveryCodeViewModel(string code, IFileSaver saver, Action<string> copyToClipboard)
    {
        Code = code;
        Copy = new RelayCommand(() => copyToClipboard(code));
        SaveAsTextFile = new RelayCommand(() =>
        {
            if (saver.Ask("PowerLedger-recovery-code.txt", "Text file|*.txt") is { } path) File.WriteAllText(path, code);
        });
    }

    public string Code { get; }

    public ICommand Copy { get; }

    public ICommand SaveAsTextFile { get; }
}
