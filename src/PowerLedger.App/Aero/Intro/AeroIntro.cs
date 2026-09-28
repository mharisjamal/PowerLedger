using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PowerLedger.App.Aero;

/// <summary>Where the intro's video and its poster are: installed next to the App's exe, under Assets\Intro.</summary>
internal sealed record IntroMedia(string Video, string Poster)
{
    public static IntroMedia Installed { get; } = new(
        Path.Combine(AppContext.BaseDirectory, "Assets", "Intro", "aero-intro.mp4"),
        Path.Combine(AppContext.BaseDirectory, "Assets", "Intro", "aero-intro.jpg"));
}

/// <summary>What plays the intro: MediaElement in the App (<see cref="MediaIntroPlayer"/>), a fake in a test, so no
/// test needs Media Foundation. Any call may throw where Windows can't play video (Windows N without the Media Feature
/// Pack); <see cref="AeroIntro"/> takes that as a failure.</summary>
internal interface IIntroPlayer : IDisposable
{
    /// <summary>The video reached its end.</summary>
    event Action? Ended;

    /// <summary>The video couldn't be opened or played (MediaElement's MediaFailed).</summary>
    event Action? Failed;

    /// <summary>What the window shows for the video, or null for none.</summary>
    UIElement? View { get; }

    bool Muted { get; set; }

    void Play();

    /// <summary>Back to the first frame, for Replay.</summary>
    void Rewind();

    void Stop();
}

/// <summary>Where the intro is: not showing, on its poster waiting for Play, playing, or at its end offering Replay.</summary>
internal enum IntroState
{
    Closed,
    Poster,
    Playing,
    Ended,
}

/// <summary>
/// Plan S intro: the Aero intro video's logic, kept out of the window so a test can drive it. It is <see cref="Due"/> on
/// the first Aero open after the move to Aero, the moment the new look's banner shows, until it has been seen once; an
/// automatic start counts it as seen, whatever happens next, so it autoplays only once, and a manual start (Settings'
/// Watch the Aero intro) counts nothing. It starts muted and plays at once, or waits on its poster for Play under
/// reduced motion or reduced transparency (the Glass settings or Windows'). Any failure (a missing file, a player that
/// can't be made or can't play, MediaFailed) ends it quietly, as <see cref="Failed"/>, and every end raises
/// <see cref="Finished"/> once, for the window to close the dialog and show the banner.
/// </summary>
internal sealed class AeroIntro(IntroMedia media, Func<IntroMedia, IIntroPlayer> open, SettingsViewModel settings) : ObservableObject
{
    private IntroState _state;
    private bool _muted = true;
    private IIntroPlayer? _player;

    /// <summary>A session ended: closed by the user, or failed.</summary>
    public event Action? Finished;

    public IntroMedia Media => media;

    /// <summary>The first Aero open after the move: the banner is still to show and the intro hasn't been seen.</summary>
    public bool Due => !settings.LookIntroduced && !settings.AeroIntroSeen;

    public IntroState State { get => _state; private set => SetProperty(ref _state, value); }

    public bool Muted { get => _muted; private set => SetProperty(ref _muted, value); }

    /// <summary>The session in progress was opened from Settings.</summary>
    public bool Manual { get; private set; }

    /// <summary>The last session ended because the video couldn't play.</summary>
    public bool Failed { get; private set; }

    /// <summary>The player of the session in progress, for the window to show; null when closed.</summary>
    public IIntroPlayer? Player => _player;

    /// <summary>Opens a session muted: playing at once, or on the poster when motion or transparency is reduced. An
    /// automatic one (<paramref name="manual"/> false) counts the intro seen first. A failure ends it at once.</summary>
    public void Start(bool manual)
    {
        if (State != IntroState.Closed) return;
        Manual = manual;
        Failed = false;
        if (!manual) settings.SeeAeroIntro();
        Muted = true;
        try
        {
            if (!File.Exists(media.Video)) throw new FileNotFoundException("The intro video isn't installed.", media.Video);
            _player = open(media);
            _player.Ended += OnEnded;
            _player.Failed += OnFailed;
            _player.Muted = true;
        }
        catch (Exception error) when (!IsFatal(error))
        {
            Fail();
            return;
        }
        State = IntroState.Poster;
        if (!AeroMotion.Reduced && !settings.Glass.ReduceTransparency) Play();
    }

    /// <summary>Play from the poster, or from the start again at the end.</summary>
    public void Play()
    {
        if (_player is null || State is IntroState.Closed or IntroState.Playing) return;
        try
        {
            if (State == IntroState.Ended) _player.Rewind();
            _player.Play();
        }
        catch (Exception error) when (!IsFatal(error))
        {
            Fail();
            return;
        }
        State = IntroState.Playing;
    }

    public void Replay() => Play();

    /// <summary>The speaker: sound on, or off again.</summary>
    public void ToggleMute()
    {
        if (_player is null) return;
        Muted = !Muted;
        try
        {
            _player.Muted = Muted;
        }
        catch (Exception error) when (!IsFatal(error))
        {
            Fail();
        }
    }

    /// <summary>Skip, Close, Esc or the scrim: stops the player, lets it go and raises <see cref="Finished"/>; again, nothing.</summary>
    public void Close()
    {
        if (State == IntroState.Closed && _player is null) return;
        var player = _player;
        _player = null;
        State = IntroState.Closed;
        if (player is not null)
        {
            player.Ended -= OnEnded;
            player.Failed -= OnFailed;
            try
            {
                player.Stop();
                player.Dispose();
            }
            catch (Exception error) when (!IsFatal(error))
            {
                // Going anyway: a player that can't stop has nothing more to show.
            }
        }
        Finished?.Invoke();
    }

    private void OnEnded()
    {
        if (State == IntroState.Playing) State = IntroState.Ended;
    }

    private void OnFailed() => Fail();

    private void Fail()
    {
        Failed = true;
        if (_player is null)
        {
            State = IntroState.Closed;
            Finished?.Invoke();
            return;
        }
        Close();
    }

    private static bool IsFatal(Exception error) => error is OutOfMemoryException or StackOverflowException or AccessViolationException;
}
