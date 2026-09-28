using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PowerLedger.App.Aero;
using AeroIcon = PowerLedger.App.Aero.Icon;

namespace PowerLedger.App;

/// <summary>
/// Plan S intro: the Aero intro video, in a glass dialog over the frosted scrim (the window's own dialog, A.Modal.Intro).
/// On the first open after the move to Aero it plays before the new look's banner, which waits for it; Settings' Watch
/// the Aero intro opens it again out of its button. It starts muted, with a speaker to turn the sound on, Skip (Close once
/// it ends or while it waits), Replay at the end, and Play on its poster under reduced motion or reduced transparency.
/// Esc and the scrim close it as any dialog. A video that can't play closes it quietly. <see cref="AeroIntro"/> holds
/// the logic; this draws it.
/// </summary>
internal partial class AeroWindow
{
    private readonly AeroIntro? _intro;
    private bool _introDue;
    private GlassPanel? _introModal;
    private IntroFace? _introFace;

    /// <summary>The intro video's dialog is open, for a test.</summary>
    internal bool IntroPlaying => _intro is { State: not IntroState.Closed };

    /// <summary>The first open after the move, once shown and past the wizard: the intro, before the banner.</summary>
    private void AutoPlayIntroVideo()
    {
        if (!_introDue || !IsLoaded || _shell.IsSetup) return;
        _introDue = false;
        OpenIntroVideo(trigger: null, manual: false);
    }

    /// <summary>Settings' Watch the Aero intro: the dialog out of <paramref name="trigger"/>, counted against nothing.</summary>
    internal void WatchIntroVideo(FrameworkElement? trigger) => OpenIntroVideo(trigger, manual: true);

    private void OpenIntroVideo(FrameworkElement? trigger, bool manual)
    {
        if (_intro is null || _intro.State != IntroState.Closed) return;
        _intro.Start(manual);
        if (_intro.State == IntroState.Closed) return;   // failed quietly; OnIntroVideoFinished has shown the banner
        var face = new IntroFace(_intro);
        face.Play.Click += (_, _) => _intro.Play();
        face.Replay.Click += (_, _) => _intro.Replay();
        face.Mute.Click += (_, _) => _intro.ToggleMute();
        face.Close.Click += (_, _) => _intro.Close();
        _introFace = face;
        _introModal = OpenModal(trigger, face.Root, closed: () => _intro.Close(), style: "A.Modal.Intro");
        _intro.PropertyChanged += OnIntroVideoChanged;
        Layer.SizeChanged += FitIntroVideo;
        FitIntroVideo(null, null);
        ShowIntroVideo(focus: true);
        ShowIntro();
    }

    private void OnIntroVideoChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AeroIntro.State) or nameof(AeroIntro.Muted)) ShowIntroVideo(focus: e.PropertyName == nameof(AeroIntro.State));
    }

    /// <summary>The dialog for the intro's state: the poster and Play while it waits, the video while it plays, Replay
    /// over the last frame at the end; the speaker as the sound is; the keyboard on what comes next.</summary>
    private void ShowIntroVideo(bool focus)
    {
        if (_intro is null || _introFace is not { } face) return;
        var state = _intro.State;
        face.Show(state, _intro.Muted);
        if (!focus || state == IntroState.Closed) return;
        var next = state switch
        {
            IntroState.Poster => face.Play,
            IntroState.Ended => face.Replay,
            _ => face.Close,
        };
        next.Focus();
    }

    /// <summary>The dialog at its width, or the window's less a margin in a small window; the picture at 16:9 inside it.</summary>
    private void FitIntroVideo(object? sender, SizeChangedEventArgs? e)
    {
        if (_introModal is not { } modal || _introFace is not { } face) return;
        modal.ClearValue(WidthProperty);
        var wanted = modal.Width;
        if (Layer.ActualWidth > 0) modal.Width = Math.Min(wanted, Math.Max(IntroFace.NarrowestWidth, Layer.ActualWidth - 2 * IntroFace.WindowMargin));
        var inner = modal.Width - modal.Padding.Left - modal.Padding.Right;
        face.Picture.Width = inner;
        face.Picture.Height = inner * 9 / 16;
    }

    /// <summary>A session ended: its dialog goes, if still up, and the banner shows; after the first open's, the keyboard
    /// goes to its Got it.</summary>
    private void OnIntroVideoFinished()
    {
        if (_intro is null) return;
        _intro.PropertyChanged -= OnIntroVideoChanged;
        Layer.SizeChanged -= FitIntroVideo;
        var modal = _introModal;
        _introModal = null;
        _introFace = null;
        if (modal is not null && _modal == modal) CloseModal();
        ShowIntro();
        AnnounceIntro();
        if (!_intro.Manual && LookIntro.IsVisible) GotItButton.Focus();
    }

    /// <summary>The window closing: the video stops and lets go, and the window hears no more of it.</summary>
    private void StopIntroVideo()
    {
        if (_intro is null) return;
        _intro.Finished -= OnIntroVideoFinished;
        _intro.PropertyChanged -= OnIntroVideoChanged;
        _intro.Close();
    }

    /// <summary>The intro dialog's content, built once a session: title and line, the picture (poster, video, Play and
    /// Replay), then the speaker and Skip or Close.</summary>
    private sealed class IntroFace
    {
        /// <summary>The narrowest the dialog gets, and the room it leaves each side in a small window.</summary>
        public const double NarrowestWidth = 320;

        public const double WindowMargin = 32;

        private readonly Image _poster;
        private readonly UIElement? _video;
        private readonly AeroIcon _speaker;

        public IntroFace(AeroIntro intro)
        {
            var root = new StackPanel();
            var title = new TextBlock { Text = "A quick tour of Aero", TextWrapping = TextWrapping.Wrap };
            title.SetResourceReference(StyleProperty, "A.Text.Title");
            title.SetResourceReference(TextBlock.FontSizeProperty, "A.T.Modal");
            var line = new TextBlock
            {
                Text = "Liquid glass, Insights and a watts overlay. The sound starts off.",
                TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = new Thickness(0, 6, 0, 16),
            };
            line.SetResourceReference(StyleProperty, "A.Text.Secondary");
            root.Children.Add(title);
            root.Children.Add(line);

            // The picture: the poster under the video (it shows while the video opens, and in its place while it waits),
            // rounded at the well's radius.
            Picture = new Grid { ClipToBounds = true };
            Picture.SetResourceReference(Panel.BackgroundProperty, "A.B.Well");
            Picture.SizeChanged += (_, _) =>
            {
                var r = TryFindRadius(Picture);
                Picture.Clip = new RectangleGeometry(new Rect(Picture.RenderSize), r, r);
            };
            _poster = new Image { Name = "IntroPoster", Stretch = Stretch.UniformToFill, Source = LoadPoster(intro.Media.Poster) };
            AutomationProperties.SetName(_poster, "The Aero intro's first frame");
            Picture.Children.Add(_poster);
            _video = intro.Player?.View;
            if (_video is not null)
            {
                AutomationProperties.SetName(_video, "The Aero intro video");
                Picture.Children.Add(_video);
            }
            Play = RoundButton("IntroPlay", "A.I.Play", "Play the intro", filled: true);
            Replay = RoundButton("IntroReplay", "A.I.Replay", "Replay the intro", filled: false);
            Picture.Children.Add(Play);
            Picture.Children.Add(Replay);
            root.Children.Add(Picture);

            var controls = new DockPanel { Margin = new Thickness(0, 16, 0, 0), LastChildFill = false };
            _speaker = new AeroIcon { Size = 16 };
            Mute = new Button { Name = "IntroMute", Content = _speaker };
            Mute.SetResourceReference(StyleProperty, "A.RoundGlassBtn");
            DockPanel.SetDock(Mute, Dock.Left);
            Close = new Button { Name = "IntroClose" };
            Close.SetResourceReference(StyleProperty, "A.AccentBtn");
            DockPanel.SetDock(Close, Dock.Right);
            controls.Children.Add(Mute);
            controls.Children.Add(Close);
            root.Children.Add(controls);
            Root = root;
        }

        public FrameworkElement Root { get; }

        public Grid Picture { get; }

        public Button Play { get; }

        public Button Replay { get; }

        public Button Mute { get; }

        public Button Close { get; }

        public void Show(IntroState state, bool muted)
        {
            if (_video is not null) _video.Visibility = state is IntroState.Playing or IntroState.Ended ? Visibility.Visible : Visibility.Collapsed;
            Play.Visibility = state == IntroState.Poster ? Visibility.Visible : Visibility.Collapsed;
            Replay.Visibility = state == IntroState.Ended ? Visibility.Visible : Visibility.Collapsed;
            _speaker.SetResourceReference(AeroIcon.DataProperty, muted ? "A.I.Muted" : "A.I.Speaker");
            var sound = muted ? "Turn the sound on" : "Turn the sound off";
            Mute.ToolTip = sound;
            AutomationProperties.SetName(Mute, sound);
            var playing = state == IntroState.Playing;
            Close.Content = playing ? "Skip" : "Close";
            AutomationProperties.SetName(Close, playing ? "Skip the intro" : "Close the intro");
        }

        private static Button RoundButton(string name, string icon, string words, bool filled)
        {
            var face = new AeroIcon { Size = 24, Filled = filled };
            face.SetResourceReference(AeroIcon.DataProperty, icon);
            var button = new Button { Name = name, Content = face, ToolTip = words, Visibility = Visibility.Collapsed };
            button.SetResourceReference(StyleProperty, "A.Intro.PlayBtn");
            AutomationProperties.SetName(button, words);
            return button;
        }

        private static double TryFindRadius(FrameworkElement element)
            => element.TryFindResource("A.R.Well") is CornerRadius radius ? radius.TopLeft : 0;

        /// <summary>The poster, read whole into memory so the file isn't held; none when it is missing or unreadable.</summary>
        private static BitmapImage? LoadPoster(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 1280;
                image.UriSource = new Uri(path, UriKind.Absolute);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception error) when (error is IOException or NotSupportedException or UnauthorizedAccessException or InvalidOperationException
                                          or System.Runtime.InteropServices.COMException)
            {
                return null;
            }
        }
    }
}
