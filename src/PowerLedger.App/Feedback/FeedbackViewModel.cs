using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PowerLedger.App;

/// <summary>What trying to add an image came to.</summary>
internal enum FeedbackAddImageResult
{
    Added,
    TooMany,
    NotAnImage,
}

/// <summary>
/// Send feedback: a message, up to five images, the last 300 lines of the App's and the service's log when the tick is
/// on, and an optional e-mail, posted to <see cref="FeedbackEndpoint"/> through <see cref="FeedbackSender"/>. No sign-in
/// and no consent are asked; the window's own caption text says where it goes.
/// </summary>
internal sealed class FeedbackViewModel : ObservableObject
{
    public const int MaxTextLength = 10_000;
    public const int MaxImages = 5;

    /// <summary>The counter shows once this few characters are left, so it stays out of the way otherwise.</summary>
    public const int CounterThreshold = 1_000;

    private readonly FeedbackSender _sender;
    private readonly UiThreads _threads;
    private readonly Func<string?> _readLog;
    private string _text = "";
    private string _email = "";
    private bool _attachLog = true;
    private bool _busy;
    private string? _message;
    private int _nextImageNumber = 1;

    public FeedbackViewModel(FeedbackSender sender, UiThreads threads, Func<string?> readLog)
    {
        _sender = sender;
        _threads = threads;
        _readLog = readLog;
        Send = new RelayCommand(() => _ = SendAsync(), () => !Busy && Text.Trim().Length > 0);
        RemoveImage = new RelayCommand<FeedbackImageItem>(item =>
        {
            if (item is not null && Images.Remove(item)) OnPropertyChanged(nameof(CanAddMoreImages));
        });
        Cancel = new RelayCommand(() => Closed?.Invoke(null));
    }

    public ObservableCollection<FeedbackImageItem> Images { get; } = [];

    public bool CanAddMoreImages => Images.Count < MaxImages;

    /// <summary>What's typed, up to <see cref="MaxTextLength"/> (the window's TextBox also enforces this as MaxLength,
    /// so this is a backstop, not the only guard).</summary>
    public string Text
    {
        get => _text;
        set
        {
            var trimmed = value.Length > MaxTextLength ? value[..MaxTextLength] : value;
            if (!SetProperty(ref _text, trimmed)) return;
            OnPropertyChanged(nameof(RemainingCharacters));
            OnPropertyChanged(nameof(ShowCounter));
            Send.NotifyCanExecuteChanged();
        }
    }

    public int RemainingCharacters => MaxTextLength - _text.Length;

    public bool ShowCounter => RemainingCharacters <= CounterThreshold;

    /// <summary>Ticked by default: attaches the last 300 lines of the App's log, and of the service's if it can be read.</summary>
    public bool AttachLog { get => _attachLog; set => SetProperty(ref _attachLog, value); }

    /// <summary>Optional, for a reply; sent as null when left blank.</summary>
    public string Email { get => _email; set => SetProperty(ref _email, value); }

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (SetProperty(ref _busy, value)) Send.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Why the last Send didn't go through, or null.</summary>
    public string? Message { get => _message; private set => SetProperty(ref _message, value); }

    public IRelayCommand Send { get; }

    public IRelayCommand<FeedbackImageItem> RemoveImage { get; }

    public IRelayCommand Cancel { get; }

    /// <summary>Sent or saved for later, or Cancel: the window closes either way. The string is what a tray notification
    /// should say, since the window is already gone by the time it would show; null for Cancel, which says nothing.</summary>
    public event Action<string?>? Closed;

    /// <summary>Adds an image already read as bytes — from Add image…, a paste, a drop, or the App's own screenshot —
    /// downscaling and re-encoding it first (review round: at most five, each brought under 1 MB). The name always ends
    /// in what the re-encoded content type calls for, never whatever the original file was named, and is never reused
    /// even once an earlier image is removed (review round: the Worker's feedback route needs every name unique).</summary>
    public FeedbackAddImageResult TryAddImage(byte[] bytes)
    {
        if (Images.Count >= MaxImages) return FeedbackAddImageResult.TooMany;
        if (ImageProcessing.Process(bytes) is not { } processed) return FeedbackAddImageResult.NotAnImage;
        var name = $"image-{_nextImageNumber++}{Extension(processed.ContentType)}";
        Images.Add(new FeedbackImageItem(name, processed.ContentType, processed.Data, FeedbackImageItem.DecodeThumbnail(processed.Data)));
        OnPropertyChanged(nameof(CanAddMoreImages));
        return FeedbackAddImageResult.Added;
    }

    private static string Extension(string contentType) => contentType == "image/jpeg" ? ".jpg" : ".png";

    private async Task SendAsync()
    {
        Busy = true;
        Message = null;
        var report = BuildReport();
        var result = await _sender.SendAsync(report).ConfigureAwait(false);
        _threads.Post(() =>
        {
            Busy = false;
            if (result.Outcome == FeedbackOutcome.Failed) Message = result.Message;
            else Closed?.Invoke(result.Message);
        });
    }

    /// <summary>Everything the window's own static line promises is also sent: the app version with its +sha, Windows'
    /// own name, and this PC's architecture — apart from the message, the images and the log, which the user chose.</summary>
    internal FeedbackReport BuildReport() => new(
        Text.Trim(),
        string.IsNullOrWhiteSpace(Email) ? null : Email.Trim(),
        HostInfo.FullVersion(),
        HostInfo.OsName(),
        HostInfo.ArchName(RuntimeInformation.OSArchitecture),
        AttachLog ? _readLog() : null,
        [.. Images.Select(image => new FeedbackImagePayload(image.Name, image.ContentType, Convert.ToBase64String(image.Data)))]);
}
