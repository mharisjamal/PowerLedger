using System.IO;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PowerLedger.Contracts;

namespace PowerLedger.App;

/// <summary>
/// A recovery code, shown once (households design §7, Plan N task A7, task 0.8): pushed as a
/// <see cref="NoticeKind.RecoveryCode"/> notice, made the first time an account links a household or whenever
/// <see cref="HouseholdStatus.RecoveryMissing"/> asks for a new one. Copy and Save as text file are the only way to
/// keep it, since the App never stores it itself. However the window closes — OK or otherwise — it answers the
/// prompt, so the service can stop holding the code for this PC (review finding A6).
/// </summary>
internal sealed class RecoveryCodeViewModel : ObservableObject
{
    public const string Explanation =
        "Keep this code safe. With it and your account you can get your household back if you lose every PC.";

    private readonly IServiceLink _link;
    private readonly string? _promptId;
    private bool _answered;
    private string? _message;

    public RecoveryCodeViewModel(IServiceLink link, HouseholdNotice notice, IFileSaver saver, Action<string> copyToClipboard)
    {
        _link = link;
        _promptId = notice.PromptId;
        Code = notice.RecoveryCode ?? "";
        Copy = new RelayCommand(() => copyToClipboard(Code));
        SaveAsTextFile = new RelayCommand(() => Save(saver));
        Ok = new RelayCommand(() => Closed?.Invoke());
    }

    public string Code { get; }

    public ICommand Copy { get; }

    public ICommand SaveAsTextFile { get; }

    public IRelayCommand Ok { get; }

    /// <summary>Why the last save didn't work, or null (review finding A6).</summary>
    public string? Message { get => _message; private set => SetProperty(ref _message, value); }

    /// <summary>OK was pressed: the window closes. Its own Closed event, firing however it actually closes, is what
    /// answers the prompt — see <see cref="Answer"/>.</summary>
    public event Action? Closed;

    /// <summary>The window is closing, by OK or otherwise: tells the service it can stop holding this code for this PC
    /// (task 0.8). Safe to call more than once; only the first counts.</summary>
    public void Answer()
    {
        if (_answered || _promptId is not { } promptId) return;
        _answered = true;
        _ = _link.AnswerPromptAsync(promptId, true);
    }

    private void Save(IFileSaver saver)
    {
        try
        {
            if (saver.Ask("PowerLedger-recovery-code.txt", "Text file|*.txt") is { } path) File.WriteAllText(path, Code);
        }
        catch (IOException error)
        {
            Message = error.Message;
        }
        catch (UnauthorizedAccessException error)
        {
            Message = error.Message;
        }
    }
}
