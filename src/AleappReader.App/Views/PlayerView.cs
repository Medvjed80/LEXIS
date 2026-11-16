using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using AleappReader.Core;

namespace AleappReader.App.Views;

/// <summary>Plays an audio or video file in place: play, pause, stop, a time bar to seek, position and length.
/// Collapsed when the file is not a playable one present on disk. Audio plays from the preview (a chat bubble, the
/// details, a gallery tile, the file viewer) without opening the file in its own window. Compact: play, pause and the
/// time only. AudioOnly: a video is not played here (a chat bubble opens it in its window instead).</summary>
public sealed class PlayerView : UserControl
{
    public static readonly DependencyProperty MediaProperty = DependencyProperty.Register(nameof(Media), typeof(MediaRef), typeof(PlayerView),
        new PropertyMetadata(null, (d, _) => ((PlayerView)d).Load()));
    public MediaRef? Media { get => (MediaRef?)GetValue(MediaProperty); set => SetValue(MediaProperty, value); }

    public static readonly DependencyProperty VideoHeightProperty = DependencyProperty.Register(nameof(VideoHeight), typeof(double), typeof(PlayerView),
        new PropertyMetadata(double.PositiveInfinity));
    public double VideoHeight { get => (double)GetValue(VideoHeightProperty); set => SetValue(VideoHeightProperty, value); }

    public static readonly DependencyProperty AudioOnlyProperty = DependencyProperty.Register(nameof(AudioOnly), typeof(bool), typeof(PlayerView),
        new PropertyMetadata(false, (d, _) => ((PlayerView)d).Load()));
    /// <summary>Plays audio only (a chat bubble or a tile: a video there opens in its window instead).</summary>
    public bool AudioOnly { get => (bool)GetValue(AudioOnlyProperty); set => SetValue(AudioOnlyProperty, value); }

    public static readonly DependencyProperty CompactProperty = DependencyProperty.Register(nameof(Compact), typeof(bool), typeof(PlayerView),
        new PropertyMetadata(false, (d, _) => ((PlayerView)d).ApplyCompact()));
    /// <summary>Play, pause and the time only, for a tile.</summary>
    public bool Compact { get => (bool)GetValue(CompactProperty); set => SetValue(CompactProperty, value); }

    readonly MediaElement _me = new() { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, ScrubbingEnabled = true };
    readonly Slider _seek = new() { Minimum = 0, IsMoveToPointEnabled = true, VerticalAlignment = VerticalAlignment.Center, MinWidth = 80, Margin = new Thickness(6, 0, 6, 0) };
    readonly TextBlock _time = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 11, Text = "0:00" };
    readonly TextBlock _note = new() { FontSize = 11, Opacity = 0.75, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    bool _moving; // the bar follows the playback: not a seek
    bool _wantPlay, _opened;
    int _load; // a newer file drops a conversion still running for the previous one

    public PlayerView()
    {
        _me.SetBinding(MaxHeightProperty, new System.Windows.Data.Binding(nameof(VideoHeight)) { Source = this });
        Button B(string glyph, string tip, Action act)
        {
            var b = new Button { Style = (Style)FindResource("FlatButton"), ToolTip = tip, Focusable = false,
                Content = new TextBlock { Style = (Style)FindResource("Icon"), Text = glyph } };
            b.Click += (_, _) => act();
            return b;
        }
        var bar = new DockPanel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(B("", "Play", () => { _wantPlay = true; _ = Start(); }));
        buttons.Children.Add(B("", "Pause", () => { _wantPlay = false; _me.Pause(); }));
        buttons.Children.Add(B("", "Stop", () => { _wantPlay = false; _me.Stop(); _tick.Stop(); Show(); }));
        DockPanel.SetDock(buttons, Dock.Left);
        DockPanel.SetDock(_time, Dock.Right);
        bar.Children.Add(buttons);
        bar.Children.Add(_time);
        bar.Children.Add(_seek);
        var root = new StackPanel();
        root.Children.Add(_me);
        root.Children.Add(bar);
        root.Children.Add(_note);
        Content = root;
        _me.MediaOpened += (_, _) =>
        {
            _opened = true;
            _seek.Maximum = _me.NaturalDuration.HasTimeSpan ? _me.NaturalDuration.TimeSpan.TotalSeconds : 0;
            Show();
            if (_wantPlay) Begin();
        };
        _me.MediaEnded += (_, _) => { _wantPlay = false; _me.Stop(); _tick.Stop(); Show(); };
        _me.MediaFailed += (_, e) => { _wantPlay = false; _time.Text = "Cannot play: " + e.ErrorException.Message; };
        _tick.Tick += (_, _) => Show();
        _seek.ValueChanged += (_, e) => { if (!_moving) { _me.Position = TimeSpan.FromSeconds(e.NewValue); Show(); } };
        Unloaded += (_, _) => { _tick.Stop(); _me.Close(); };
        Visibility = Visibility.Collapsed;
    }

    void ApplyCompact() => _seek.Visibility = Compact ? Visibility.Collapsed : Visibility.Visible;

    static string T(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    void Show()
    {
        _moving = true;
        _seek.Value = Math.Min(_seek.Maximum, _me.Position.TotalSeconds);
        _moving = false;
        _time.Text = $"{T(_me.Position)} / {(_me.NaturalDuration.HasTimeSpan ? T(_me.NaturalDuration.TimeSpan) : "?")}";
    }

    void Begin()
    {
        if (!_wantPlay) return;
        _wantPlay = false;
        _me.Play();
        _tick.Start();
    }

    async Task Start()
    {
        if (!await Open()) { _wantPlay = false; return; }
        if (_opened) Begin(); // already open: MediaOpened will not fire again
    }

    void Load()
    {
        _tick.Stop();
        _load++;
        _wantPlay = false;
        _opened = false;
        _seek.Maximum = 0;
        _time.Text = "0:00";
        _time.ToolTip = null;
        _note.Visibility = Visibility.Collapsed;
        _me.Stop();
        _me.Source = null;
        var m = Media;
        var video = m?.Kind == "video" && !AudioOnly;
        var playable = m is { Exists: true } && (m.Kind == "audio" || video);
        // A collapsed player never produces sound. Audio keeps a pixel so it can play, without covering the preview.
        if (video)
        {
            _me.ClearValue(WidthProperty);
            _me.ClearValue(HeightProperty);
            _me.Opacity = 1;
            _me.Visibility = Visibility.Visible;
            _me.IsHitTestVisible = true;
        }
        else
        {
            _me.Width = 1;
            _me.Height = 1;
            _me.Opacity = 0;
            _me.Visibility = Visibility.Visible;
            _me.IsHitTestVisible = false;
        }
        Visibility = playable ? Visibility.Visible : Visibility.Collapsed;
        ApplyCompact();
        if (playable && video) _ = ShowFrame();
    }

    async Task ShowFrame()
    {
        if (await Open()) { _me.Play(); _me.Pause(); } // shows the first frame and the length
    }

    /// <summary>The file opened in the player, on first play for audio: a portable case's is written out of the zip then.
    /// SILK is decoded, and a format the player cannot read (Opus, AMR, Ogg, …) is converted to WAV
    /// (<see cref="AudioPlayback"/>). The original is only read.</summary>
    async Task<bool> Open()
    {
        if (_me.Source is not null) return true;
        if (Media is not { } m) return false;
        var load = _load;
        try
        {
            _time.Text = "Opening…";
            string file;
            string? note;
            if (m.Kind == "audio") (file, note) = await AudioPlayback.Prepare(m);
            else
            {
                // a video plays as itself; SILK is audio and is handled above
                var (path, sha) = await Task.Run(() => m.AudioFolders is { Length: > 0 } f ? SilkAudio.Playable(m, f) : (m.LocalFile(), (string?)null));
                file = path;
                note = sha is null ? null : $"SILK audio, converted to WAV for listening; original unchanged.\nSHA-256 {sha}\nWAV: {file}\nDecoder: {SilkAudio.Decoder}";
            }
            if (Media != m || load != _load) return false;
            _me.Source = new Uri(file);
            if (note is not null)
            {
                _note.Text = note;
                _time.ToolTip = note;
                _note.Visibility = Compact ? Visibility.Collapsed : Visibility.Visible;
            }
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException or System.IO.InvalidDataException
                                      or DllNotFoundException or BadImageFormatException or AiException)
        {
            if (Media == m && load == _load)
            {
                _time.Text = e is System.IO.IOException or UnauthorizedAccessException ? "Cannot read the file" : "Cannot play";
                _time.ToolTip = e.Message;
                _note.Text = e.Message; // ffmpeg's own text, not only its exit code
                _note.Visibility = Visibility.Visible;
            }
        }
        return _me.Source is not null;
    }
}
