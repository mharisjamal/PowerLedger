using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PowerLedger.App.Aero;

/// <summary>
/// The intro's real player: a WPF MediaElement, driven by hand (LoadedBehavior and UnloadedBehavior Manual) so nothing
/// plays until <see cref="AeroIntro"/> says so, and closed when it is let go. Windows plays the H.264 and AAC through Media
/// Foundation; where there is none (Windows N without the Media Feature Pack) opening or playing throws or raises
/// MediaFailed, which the intro takes as a quiet failure.
/// </summary>
internal sealed class MediaIntroPlayer : IIntroPlayer
{
    private readonly MediaElement _element;

    private MediaIntroPlayer(IntroMedia media)
    {
        _element = new MediaElement
        {
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Manual,
            Stretch = Stretch.Uniform,
            IsMuted = true,
            ScrubbingEnabled = false,
            Focusable = false,
        };
        _element.MediaEnded += (_, _) => Ended?.Invoke();
        _element.MediaFailed += (_, _) => Failed?.Invoke();
        _element.Source = new Uri(media.Video, UriKind.Absolute);
    }

    public event Action? Ended;

    public event Action? Failed;

    public UIElement View => _element;

    public bool Muted { get => _element.IsMuted; set => _element.IsMuted = value; }

    /// <summary>A player for <paramref name="media"/>'s video; call on the UI thread.</summary>
    public static IIntroPlayer Open(IntroMedia media) => new MediaIntroPlayer(media);

    public void Play() => _element.Play();

    public void Rewind() => _element.Position = TimeSpan.Zero;

    public void Stop() => _element.Stop();

    public void Dispose() => _element.Close();
}
