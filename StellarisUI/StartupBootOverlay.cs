using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace StellarisUI;

/// <summary>
/// A short native startup layer. Its scan reveals the existing desktop underneath;
/// it does not host a browser or construct a second, imitation desktop.
/// </summary>
internal sealed class StartupBootOverlay : FrameworkElement, IDisposable
{
	// Exact PCM duration of the user's 2026-10-13 recording (309888 bytes / 44100).
	internal const double VoiceDuration = 7.026938775510204;
	internal const double TransitionStart = VoiceDuration + 0.122608;
	internal const double TransitionEnd = TransitionStart + 1.4;
	internal const double Duration = TransitionEnd + 0.8;
	private const double DesignWidth = 1600;
	private const double DesignHeight = 900;
	private const string Ink = "#F1F2F2";
	private const string Secondary = "#B1B5B7";
	private const string Muted = "#92989C";
	private const string Accent = "#83EAF5";
	private const double Intensity = 0.85;
	private static readonly Dictionary<string, SolidColorBrush> BrushesByColor = new();
	private static readonly Dictionary<string, Pen> Pens = new();
	private static readonly Typeface Sans = new(new FontFamily("Arial"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
	// Bahnschrift (Windows 10+) gives the condensed industrial look; Arial is the fallback.
	private static readonly Typeface SansBold = new(new FontFamily("Bahnschrift, Arial"), FontStyles.Normal, FontWeights.Bold, FontStretches.Condensed);
	private static readonly Typeface Mono = new("Consolas");
	private readonly string _voicePath;
	private readonly BitmapSource? _logo;
	private readonly MediaPlayer _voice = new();
	private readonly CancellationTokenSource _lifetime = new();
	private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly TaskCompletionSource<bool> _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly Stopwatch _overall = new();
	private readonly Stopwatch _playClock = new();
	private readonly Stopwatch _tailClock = new();
	private readonly DispatcherTimer _tickTimer;
	private readonly Dictionary<(string Value, double Size, bool Bold, bool Mono, string Color, double Dpi), FormattedText> _textCache = new();
	private bool _started;
	private bool _finished;
	private bool _disposed;
	private bool _mediaPlaying;
	private bool _fallbackClock;
	private bool _transitionLogged;
	private readonly bool _reducedMotion;
	private double _time;
	private double _fallbackAnchor;
	private double _fallbackWallAnchor;
	private double _lastAudioPosition;
	private double _lastAudioAdvanceWall;
	private double _tailAnchor;
	private double? _testTime;
	private DrawingGroup? _transitionLayer;
	private Size _transitionSize;
	private bool _compact;
	private bool _narrow;
	private double _scale = 1;
	private double _pixelsPerDip = 1;

	public StartupBootOverlay(string baseDirectory)
	{
		_voicePath = Path.Combine(baseDirectory, "Assets", "startup-voice.wav");
		_reducedMotion = !SystemParameters.ClientAreaAnimation;
		_tickTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(33) };
		_tickTimer.Tick += OnTick;
		Focusable = true;
		IsHitTestVisible = true;
		try { _logo = LoadLogo(); }
		catch (Exception ex) { App.LogStartup("PRTS logo unavailable: " + ex.GetType().Name); }
		_voice.Volume = 0.9;
		_voice.MediaOpened += OnMediaOpened;
		_voice.MediaEnded += OnMediaEnded;
		_voice.MediaFailed += OnMediaFailed;
		Unloaded += OnUnloaded;
	}

	public bool IsPlaying => _started && !_finished && !_disposed;

	public Task PlayAsync()
	{
		Dispatcher.VerifyAccess();
		if (_started || _finished || _disposed) return _completion.Task;
		_started = true;
		_overall.Start();
		_tickTimer.Start();
		App.LogStartup("PRTS intro begin; native overlay; reducedMotion=" + _reducedMotion);
		_ = StartVoiceAsync();
		InvalidateVisual();
		return _completion.Task;
	}

	public void Skip()
	{
		if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(Skip)); return; }
		Finish("skipped");
	}

	public void Dispose()
	{
		if (_disposed) return;
		Dispatcher.VerifyAccess();
		Finish("disposed");
		_disposed = true;
		Unloaded -= OnUnloaded;
		_voice.MediaOpened -= OnMediaOpened;
		_voice.MediaEnded -= OnMediaEnded;
		_voice.MediaFailed -= OnMediaFailed;
		_lifetime.Dispose();
	}

	private async Task StartVoiceAsync()
	{
		try
		{
			if (!File.Exists(_voicePath)) { StartFallback("voice asset missing"); return; }
			_voice.Open(new Uri(_voicePath, UriKind.Absolute));
			Task timeout = Task.Delay(1800, _lifetime.Token);
			Task winner = await Task.WhenAny(_opened.Task, timeout);
			if (_finished || _disposed) return;
			if (winner != _opened.Task || !await _opened.Task)
			{
				StartFallback("voice could not open within deadline");
				return;
			}
			_playClock.Restart();
			_lastAudioAdvanceWall = 0;
			_mediaPlaying = true;
			_voice.Play();
			App.LogStartup("PRTS voice playing; user-provided startup recording; rate=1.0");
		}
		catch (OperationCanceledException) { }
		catch (Exception ex)
		{
			if (!_finished && !_disposed) StartFallback("voice failure: " + ex.GetType().Name);
		}
	}

	private void OnMediaOpened(object? sender, EventArgs e)
	{
		if (_finished || _disposed || _fallbackClock) return;
		App.LogStartup("PRTS voice opened; duration=" + (_voice.NaturalDuration.HasTimeSpan ? _voice.NaturalDuration.TimeSpan.TotalSeconds.ToString("F6", CultureInfo.InvariantCulture) : "unknown"));
		_opened.TrySetResult(true);
	}

	private void OnMediaEnded(object? sender, EventArgs e)
	{
		if (_finished || _disposed || _fallbackClock) return;
		_tailAnchor = Math.Max(VoiceDuration, _time);
		_tailClock.Restart();
		_mediaPlaying = false;
		App.LogStartup("PRTS voice ended; entering short transition lead-in");
	}

	private void OnMediaFailed(object? sender, ExceptionEventArgs e)
	{
		if (_finished || _disposed) return;
		_opened.TrySetResult(false);
		StartFallback("media error: " + e.ErrorException.GetType().Name);
	}

	private void StartFallback(string reason)
	{
		if (_finished || _disposed || _fallbackClock) return;
		_fallbackClock = true;
		_mediaPlaying = false;
		_fallbackAnchor = _time;
		_playClock.Restart();
		_fallbackWallAnchor = 0;
		try { _voice.Close(); } catch { }
		App.LogStartup("PRTS silent clock fallback; " + reason);
	}

	private void OnTick(object? sender, EventArgs e)
	{
		if (!IsPlaying) return;
		double next = _time;
		if (_fallbackClock) next = _fallbackAnchor + _playClock.Elapsed.TotalSeconds - _fallbackWallAnchor;
		else if (_tailClock.IsRunning) next = _tailAnchor + _tailClock.Elapsed.TotalSeconds;
		else if (_mediaPlaying)
		{
			double position = Math.Clamp(_voice.Position.TotalSeconds, 0, VoiceDuration);
			if (position > _lastAudioPosition + 0.001)
			{
				_lastAudioPosition = position;
				_lastAudioAdvanceWall = _playClock.Elapsed.TotalSeconds;
			}
			// A broken media device must never strand the desktop behind this layer.
			if (_playClock.Elapsed.TotalSeconds - _lastAudioAdvanceWall > 1.2)
			{
				_time = Math.Max(_time, position);
				StartFallback("audio clock stopped advancing");
			}
			next = Math.Max(_time, position);
		}
		_time = Math.Min(Duration, Math.Max(_time, next));
		if (!_transitionLogged && _time >= TransitionStart)
		{
			_transitionLogged = true;
			App.LogStartup("PRTS transition begin; scanning into real command desktop");
		}
		if (_time >= TransitionEnd) IsHitTestVisible = false;
		if (_time >= Duration || _overall.Elapsed.TotalSeconds > Duration + 5)
		{
			Finish(_time >= Duration ? "complete" : "safety timeout");
			return;
		}
		InvalidateVisual();
	}

	private void Finish(string reason)
	{
		if (_finished) return;
		_finished = true;
		_time = Duration;
		IsHitTestVisible = false;
		// Unlike CompositionTarget.Rendering, this clock keeps running while the
		// desktop window is minimized/occluded (including Windows + D).
		_tickTimer.Stop();
		_tickTimer.Tick -= OnTick;
		_overall.Stop();
		_playClock.Stop();
		_tailClock.Stop();
		_lifetime.Cancel();
		try { _voice.Close(); } catch { }
		_transitionLayer = null;
		InvalidateVisual();
		_completion.TrySetResult(true);
		App.LogStartup("PRTS intro " + reason + "; desktop ready");
	}

	private void OnUnloaded(object sender, RoutedEventArgs e) => Finish("unloaded");

	protected override void OnPreviewKeyDown(KeyEventArgs e)
	{
		if (e.Key == Key.Escape && IsPlaying) { Skip(); e.Handled = true; }
		base.OnPreviewKeyDown(e);
	}

	protected override void OnRender(DrawingContext dc)
	{
		base.OnRender(dc);
		double t = _testTime ?? _time;
		if (t >= TransitionEnd || ActualWidth <= 0 || ActualHeight <= 0) return;
		_pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
		if (_reducedMotion && _testTime is null)
		{
			// Keep voice and readable states while omitting packets, glitch and wipe.
			if (t < TransitionStart) DrawBoot(dc, Math.Min(t, TransitionStart), true);
			return;
		}
		if (t <= TransitionStart) { DrawBoot(dc, t, false); return; }
		DrawTransition(dc, t);
	}

	// ---------------------------------------------------------------------------------------------
	// Visual sequence (2026-10-13 rev): frame rails → Rhodes Island logo + PRTS wordmark assemble →
	// identity panel (operator, 8-digit passcode, biometric ring) → ACCESS GRANTED + module bars →
	// WELCOME BACK / big load percentage → slanted blade wipe that reveals the real desktop.
	// All timing anchors (0.9 / 1.4 / 3.35 / 4.65, password 1.5–3.15, voice-driven transition) are
	// unchanged; only drawing changed.
	// ---------------------------------------------------------------------------------------------
	private const string Bg = "#0A0B0C";
	private const string Panel = "#121416";
	private const string PanelLine = "#3A4046";
	private const string Slot = "#2B3035";
	private static readonly string[] BootLog =
	[
		"mount /archive .............. ok", "link dialogue.core .......... ok", "audio.bus 22050Hz mono ...... ok",
		"probe display ............. 4 ws", "load nanite.interface ....... ok", "sync qqmusic.bridge ......... ok",
		"verify sprites [16] ......... ok", "touch.map ahoge/body/chest .. ok", "restore ambient.mood ........ ok",
		"handoff -> PRTS auth"
	];
	private static readonly (string Name, double At)[] Modules =
	[
		("INTERFACE CORE", 3.50), ("DIALOGUE LINK", 3.62), ("ARCHIVE MODULE", 3.74), ("AUDIO CHANNEL", 3.86), ("GRAY WIND", 3.98)
	];
	private static RadialGradientBrush? _vignette;

	private void DrawTransition(DrawingContext dc, double t)
	{
		Size size = new(ActualWidth, ActualHeight);
		if (_transitionLayer is null || _transitionSize != size)
		{
			_transitionLayer = new DrawingGroup();
			using (DrawingContext layer = _transitionLayer.Open()) DrawBoot(layer, TransitionStart, false);
			_transitionLayer.Freeze();
			_transitionSize = size;
		}
		double q = Progress(t, TransitionStart, TransitionEnd);
		double p = q < 0.5 ? 4 * q * q * q : 1 - Math.Pow(-2 * q + 2, 3) / 2;
		double w = ActualWidth, h = ActualHeight, u = Math.Max(0.4, Math.Min(w / DesignWidth, h / DesignHeight));
		double slope = 0.42;
		double edge = -60 * u + p * (w + slope * h + 300 * u);
		double Ex(double y) => edge - y * slope;
		if (Ex(0) < w)
		{
			// Everything left of the blade is already the real desktop (this layer is transparent there).
			dc.PushClip(Quad(new Point(Ex(0), 0), new Point(w + 2, 0), new Point(w + 2, h), new Point(Ex(h), h)));
			dc.DrawDrawing(_transitionLayer);
			dc.Pop();
		}
		Blade(dc, Ex, h, 0, 70 * u, Ink, 0.95);
		Blade(dc, Ex, h, -46 * u, 10 * u, Accent, 0.9);
		Blade(dc, Ex, h, -110 * u, 4 * u, Accent, 0.6);
		Blade(dc, Ex, h, -170 * u, 2 * u, Ink, 0.4);
		dc.PushClip(Quad(new Point(Ex(0) + 70 * u, 0), new Point(Ex(0) + 96 * u, 0), new Point(Ex(h) + 96 * u, h), new Point(Ex(h) + 70 * u, h)));
		dc.DrawRectangle(Brush(Accent), null, new Rect(0, 0, w, h));
		for (double y = -40 * u; y < h + 40 * u; y += 28 * u)
		{
			double x = Ex(y) + 70 * u;
			dc.DrawGeometry(Brush(Bg), null, Quad(new Point(x - 30 * u, y), new Point(x + 60 * u, y - 30 * u),
				new Point(x + 60 * u, y - 16 * u), new Point(x - 30 * u, y + 14 * u)));
		}
		dc.Pop();
	}

	private static void Blade(DrawingContext dc, Func<double, double> ex, double h, double offset, double width, string color, double alpha)
	{
		dc.PushOpacity(alpha);
		dc.DrawGeometry(Brush(color), null, Quad(new Point(ex(0) + offset, 0), new Point(ex(0) + offset + width, 0),
			new Point(ex(h) + offset + width, h), new Point(ex(h) + offset, h)));
		dc.Pop();
	}

	private static StreamGeometry Quad(Point a, Point b, Point c, Point d) => Poly(a, b, c, d);

	private static StreamGeometry Poly(params Point[] points)
	{
		StreamGeometry geometry = new();
		using (StreamGeometryContext context = geometry.Open())
		{
			context.BeginFigure(points[0], true, true);
			for (int i = 1; i < points.Length; i++) context.LineTo(points[i], false, false);
		}
		geometry.Freeze();
		return geometry;
	}

	private void DrawBoot(DrawingContext dc, double t, bool reduced)
	{
		_scale = Math.Min(ActualWidth / DesignWidth, ActualHeight / DesignHeight);
		_compact = ActualWidth < 1000;
		_narrow = ActualWidth < 640;
		dc.DrawRectangle(Brush(Bg), null, new Rect(0, 0, ActualWidth, ActualHeight));
		dc.PushTransform(new TranslateTransform((ActualWidth - DesignWidth * _scale) / 2, (ActualHeight - DesignHeight * _scale) / 2));
		dc.PushTransform(new ScaleTransform(_scale, _scale));
		dc.PushOpacity(0.06);
		for (double y = 0; y < DesignHeight; y += 4) Rect(dc, 0, y, DesignWidth, 1, "#5B6166");
		dc.Pop();
		DrawFrame(dc, t, reduced);
		DrawTopBar(dc, t);
		if (!_narrow) DrawBootLog(dc, t);
		DrawIdentity(dc, t, reduced);
		DrawAuthPanel(dc, t, reduced);
		DrawWelcome(dc, t, reduced);
		dc.Pop(); dc.Pop();
		_vignette ??= CreateVignette();
		dc.DrawRectangle(_vignette, null, new Rect(0, 0, ActualWidth, ActualHeight));
	}

	private static RadialGradientBrush CreateVignette()
	{
		RadialGradientBrush brush = new() { GradientOrigin = new Point(0.5, 0.5), Center = new Point(0.5, 0.5), RadiusX = 0.75, RadiusY = 0.75 };
		brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.45));
		brush.GradientStops.Add(new GradientStop(Color.FromArgb(140, 0, 0, 0), 1));
		brush.Freeze();
		return brush;
	}

	private static void DrawFrame(DrawingContext dc, double t, bool reduced)
	{
		double p1 = reduced ? 1 : EaseOut(Progress(t, 0, 0.25)), p2 = reduced ? 1 : EaseInOut(Progress(t, 0.28, 0.72));
		if (!reduced && t < 0.28) { Rect(dc, 800 - 800 * p1, 449, 1600 * p1, 2, Ink); return; }
		double top = 450 - 380 * p2, bottom = 450 + 380 * p2;
		dc.PushOpacity(1 - 0.55 * p2);
		Rect(dc, 0, top, DesignWidth, 2, Ink);
		Rect(dc, 0, bottom - 2, DesignWidth, 2, Ink);
		dc.Pop();
		dc.PushOpacity(0.5 * p2);
		for (int x = 40; x < DesignWidth; x += 40)
		{
			double tick = x % 200 == 0 ? 10 : 5;
			Rect(dc, x, top + 2, 1, tick, Secondary);
			Rect(dc, x, bottom - 2 - tick, 1, tick, Secondary);
		}
		dc.Pop();
	}

	private void DrawTopBar(DrawingContext dc, double t)
	{
		if (t < 0.3) return;
		dc.PushOpacity(EaseOut(Progress(t, 0.3, 0.5)));
		Rect(dc, 40, 34, 8, 22, Ink);
		Text(dc, "PRTS", 60, 56, 26, Ink, bold: true);
		if (!_narrow) Text(dc, "//  GRAY NANITE TERMINAL", 132, 54, 15, Secondary, mono: true);
		Text(dc, "BOOT SEQ  T+" + Math.Min(t, Duration).ToString("00.000", CultureInfo.InvariantCulture), 1560, 54, 15, Secondary, TextAlignment.Right, mono: true);
		if ((int)Math.Floor(t * 4) % 2 == 0) Rect(dc, 1340, 43, 9, 9, Accent);
		if (!_narrow) Text(dc, "ESC / SKIP", 1560, 876, 14, Muted, TextAlignment.Right, mono: true);
		dc.Pop();
	}

	private void DrawBootLog(DrawingContext dc, double t)
	{
		if (t < 0.18 || t >= 1.9) return;
		double fade = 1 - Progress(t, 1.5, 1.9);
		int shown = 0;
		for (int i = 0; i < BootLog.Length; i++) if (t > 0.18 + i * 0.1) shown = i + 1;
		int first = Math.Max(0, shown - 6);
		for (int i = first; i < shown; i++)
		{
			int row = i - first, count = shown - first;
			dc.PushOpacity(fade * (0.35 + 0.65 * (row + 1) / count));
			Text(dc, BootLog[i], 52, 700 + row * 22, 14, i == shown - 1 ? Ink : Muted, mono: true);
			dc.Pop();
		}
	}

	private void DrawIdentity(DrawingContext dc, double t, bool reduced)
	{
		if (t < 0.1) return;
		double dock = reduced ? (t >= 1.4 ? 1 : 0) : EaseInOut(Progress(t, 1.4, 1.78));
		double leave = reduced ? (t >= 4.65 ? 1 : 0) : EaseInOut(Progress(t, 4.65, 4.95));
		if (leave >= 1) return;
		double cx = 800 - 360 * dock - 500 * leave, cy = 318 - 10 * dock, s = 1 - 0.2 * dock;
		dc.PushOpacity(1 - leave);
		dc.PushTransform(new TranslateTransform(cx, cy));
		dc.PushTransform(new ScaleTransform(s, s));
		// Rings and brackets around the logo.
		double ringIn = reduced ? 1 : EaseOut(Progress(t, 0.5, 1.1));
		double spin = reduced ? 0 : t * 0.6;
		dc.PushOpacity(0.55 * ringIn);
		ArcStroke(dc, new Point(0, 0), 142, -Math.PI / 2 + spin, -Math.PI / 2 + spin + Math.PI * 1.35 * ringIn, Secondary, 1.5);
		ArcStroke(dc, new Point(0, 0), 156, Math.PI / 2 - spin * 1.5, Math.PI / 2 - spin * 1.5 + 0.55, Accent, 2);
		for (int i = 0; i < 48; i++)
		{
			if (i / 48.0 > ringIn) break;
			double a = i * Math.PI * 2 / 48 + (reduced ? 0 : -t * 0.15);
			double r1 = 120, r2 = i % 4 == 0 ? 130 : 125;
			Line(dc, Math.Cos(a) * r1, Math.Sin(a) * r1, Math.Cos(a) * r2, Math.Sin(a) * r2, i % 4 == 0 ? Ink : "#61818A", 1.2);
		}
		dc.Pop();
		double bracket = reduced ? 1 : EaseOut(Progress(t, 0.35, 0.7));
		dc.PushOpacity(bracket);
		double g = 104 + 20 * (1 - bracket);
		foreach (int sx in new[] { -1, 1 })
			foreach (int sy in new[] { -1, 1 })
			{
				Line(dc, sx * g, sy * g, sx * (g - 26), sy * g, Accent, 2.4);
				Line(dc, sx * g, sy * g, sx * g, sy * (g - 26), Accent, 2.4);
			}
		dc.Pop();
		DrawLogoReveal(dc, t, reduced);
		// Wordmark and subtitle.
		const string letters = "PRTS", scrambled = "K7X9RTSA04";
		string shown = "";
		for (int i = 0; i < letters.Length; i++)
			shown += reduced || t >= 0.62 + i * 0.12 ? letters[i] : t >= 0.3 + i * 0.05 ? scrambled[((int)(t * 26) + i * 3) % scrambled.Length] : ' ';
		double wordY = 236;
		Tracked(dc, shown, 0, wordY, 104, 22, Ink, true);
		if (!reduced)
		{
			double gl = Progress(t, 1.08, 1.2);
			if (gl > 0 && gl < 1)
			{
				dc.PushClip(new RectangleGeometry(new Rect(-260, wordY - 70, 520, 16)));
				Tracked(dc, letters, 14, wordY, 104, 22, Accent, true);
				dc.Pop();
			}
		}
		const string sub = "SYNTHESIZE INFORMATION";
		int typed = reduced ? sub.Length : (int)Math.Floor(Progress(t, 1.0, 1.4) * sub.Length);
		Tracked(dc, sub[..typed], 0, wordY + 40, 19, 6, Secondary);
		if (t >= 1.25 || reduced) Tracked(dc, "ANALYSIS OS  //  GRAY NANITE", 0, wordY + 70, 14, 5, Muted);
		dc.Pop(); dc.Pop(); dc.Pop();
	}

	private void DrawLogoReveal(DrawingContext dc, double t, bool reduced)
	{
		const double boxW = 176, boxH = 150;
		double reveal = reduced ? 1 : EaseOut(Progress(t, 0.42, 1.0));
		Rect box = new(-boxW / 2, -boxH / 2 - 6, boxW, boxH);
		if (_logo is not null && reveal > 0)
		{
			double scale = Math.Min(boxW / _logo.PixelWidth, boxH / _logo.PixelHeight);
			double w = _logo.PixelWidth * scale, h = _logo.PixelHeight * scale;
			Rect image = new(box.Left + (boxW - w) / 2, box.Top + (boxH - h) / 2, w, h);
			dc.PushClip(new RectangleGeometry(new Rect(image.Left - 2, image.Top - 2, image.Width + 4, (image.Height + 4) * reveal)));
			dc.DrawImage(_logo, image);
			dc.Pop();
			if (!reduced && reveal < 1)
			{
				double y = image.Top + image.Height * reveal;
				dc.PushOpacity(0.9);
				Rect(dc, image.Left - 18, y - 1, image.Width + 36, 2, Accent);
				dc.Pop();
			}
			if (!reduced)
			{
				// Brief RGB-split glitch slices as the logo locks in.
				double gl = Progress(t, 0.96, 1.16);
				if (gl > 0 && gl < 1)
				{
					foreach ((double top, double height, double shift) in new[] { (0.18, 0.08, 10.0), (0.47, 0.06, -14.0), (0.72, 0.1, 8.0) })
					{
						dc.PushClip(new RectangleGeometry(new Rect(image.Left - 20, image.Top + image.Height * top, image.Width + 40, image.Height * height)));
						dc.PushOpacity(0.85 * Math.Sin(Math.PI * gl));
						dc.DrawImage(_logo, new Rect(image.Left + shift, image.Top, image.Width, image.Height));
						dc.Pop(); dc.Pop();
					}
				}
				// Lock-in shockwave: an expanding accent ring rather than a filled disc.
				double wave = Progress(t, 1.0, 1.3);
				if (wave > 0 && wave < 1)
				{
					double r = 96 + 90 * EaseOut(wave);
					dc.PushOpacity((1 - wave) * 0.85);
					dc.DrawEllipse(null, Pen(Accent, 3 * (1 - wave) + 0.8), new Point(0, -6), r, r);
					dc.DrawEllipse(null, Pen(Ink, 1), new Point(0, -6), r * 0.82, r * 0.82);
					dc.Pop();
				}
			}
		}
	}

	private void DrawAuthPanel(DrawingContext dc, double t, bool reduced)
	{
		if (t < 1.42) return;
		double pin = reduced ? 1 : EaseInOut(Progress(t, 1.42, 1.8));
		double pout = reduced ? (t >= 4.65 ? 1 : 0) : EaseInOut(Progress(t, 4.55, 4.85));
		if (pout >= 1) return;
		double px = 1600 - 760 * pin + 900 * pout, py = 200, pw = 640, ph = 470;
		bool granted = t >= 3.35;
		StreamGeometry panel = Poly(new Point(px, py), new Point(px + pw - 34, py), new Point(px + pw, py + 34),
			new Point(px + pw, py + ph), new Point(px + 34, py + ph), new Point(px, py + ph - 34));
		dc.DrawGeometry(Brush(Panel), Pen(PanelLine, 1.5), panel);
		dc.PushClip(panel);
		Hazard(dc, px, py, pw, 16, granted ? Accent : "#D9DCDE", reduced ? 0 : t * 60);
		Text(dc, granted ? "ACCESS GRANTED" : "IDENTITY VERIFICATION", px + 36, py + 74, 36, granted ? Accent : Ink, bold: true);
		Text(dc, granted ? "AUTH-07 / VERIFIED" : "AUTH-07 / LOCAL", px + pw - 36, py + 70, 13, Muted, TextAlignment.Right, mono: true);
		Line(dc, px + 36, py + 92, px + pw - 36, py + 92, "#30353A");
		double fields = granted ? 1 - Progress(t, 3.35, 3.5) : 1;
		if (fields > 0)
		{
			dc.PushOpacity(fields);
			// Narrow screens inflate small labels to stay legible, so stack each label above its field there.
			double fx = _narrow ? px + 36 : px + 170, opY = _narrow ? py + 176 : py + 152, pcY = _narrow ? py + 280 : py + 204;
			Text(dc, "OPERATOR", px + 36, _narrow ? py + 134 : py + 150, 14, Muted, mono: true);
			const string op = "COMMANDER";
			int typed = reduced ? op.Length : (int)Math.Floor(Progress(t, 1.6, 1.95) * op.Length);
			Text(dc, op[..typed], fx, opY, 26, Ink, mono: true);
			Line(dc, fx, opY + 14, px + 420, opY + 14, PanelLine);
			Text(dc, "PASSCODE", px + 36, _narrow ? py + 240 : py + 222, 14, Muted, mono: true);
			int count = PasswordCount(t);
			for (int i = 0; i < 8; i++)
			{
				if (i < count) Rect(dc, fx + 2 + i * 30, pcY, 16, 16, Ink);
				else dc.DrawRectangle(null, Pen(PanelLine, 1), new Rect(fx + 2.5 + i * 30, pcY + 0.5, 15, 15));
			}
			if (count < 8 && (reduced || (int)Math.Floor(t * 5) % 2 == 0)) Rect(dc, fx + 2 + count * 30, pcY + 18, 16, 3, Accent);
			Line(dc, fx, pcY + 34, px + 420, pcY + 34, PanelLine);
			Point scanner = new(px + 530, py + 205);
			double sp = Progress(t, 1.5, 3.3);
			for (int i = 0; i < 24; i++)
			{
				double a0 = i / 24.0 * Math.PI * 2 - Math.PI / 2;
				ArcStroke(dc, scanner, 62, a0 + 0.04, a0 + Math.PI * 2 / 24 - 0.04, i / 24.0 < sp ? Accent : Slot, 6);
			}
			double spin = reduced ? 0 : t * 2.2;
			ArcStroke(dc, scanner, 46, spin, spin + 4, Secondary, 1.5);
			Text(dc, ((int)(sp * 100)).ToString("000", CultureInfo.InvariantCulture), scanner.X, scanner.Y + 9, 26, Ink, TextAlignment.Center, mono: true);
			if (!_narrow) Text(dc, "BIOMETRIC", scanner.X, scanner.Y + 96, 12, Muted, TextAlignment.Center, mono: true);
			(string Label, string Value)[] status = [("CHANNEL", "LOCAL / ENCRYPTED"), ("PROFILE", "COMMANDER"), ("CLEARANCE", count >= 8 ? "LEVEL 5" : "PENDING")];
			for (int i = 0; i < (_narrow ? 0 : status.Length); i++)
			{
				Text(dc, status[i].Label, px + 36, py + 320 + i * 30, 13, Muted, mono: true);
				Text(dc, status[i].Value, px + 200, py + 320 + i * 30, 14, i == 2 && count >= 8 ? Accent : Secondary, mono: true);
			}
			dc.Pop();
		}
		if (granted)
		{
			dc.PushOpacity(reduced ? 1 : Progress(t, 3.45, 3.6));
			for (int i = 0; i < Modules.Length; i++)
			{
				double y = py + 150 + i * 56, f = reduced ? 1 : EaseOut(Progress(t, Modules[i].At, Modules[i].At + 0.45));
				Text(dc, Modules[i].Name, px + 36, y, 16, f >= 1 ? Ink : Secondary, mono: true);
				double barW = _narrow ? 210 : 250;
				Rect(dc, px + 300, y - 10, barW, 6, Slot);
				Rect(dc, px + 300, y - 10, barW * f, 6, f >= 1 ? Accent : Ink);
				Text(dc, f >= 1 ? "ONLINE" : ((int)(f * 100)).ToString("000", CultureInfo.InvariantCulture) + "%", px + pw - 36, y, 14,
					f >= 1 ? Accent : Secondary, TextAlignment.Right, mono: true);
			}
			dc.Pop();
			double stampFade = reduced ? 0 : 1 - Progress(t, 3.9, 4.2);
			if (stampFade > 0)
			{
				double grow = 1.8 - 0.8 * EaseOut(Progress(t, 3.35, 3.5));
				dc.PushOpacity(stampFade);
				dc.PushTransform(new TranslateTransform(px + pw - 170, py + ph - 44));
				dc.PushTransform(new RotateTransform(-7));
				dc.PushTransform(new ScaleTransform(grow, grow));
				dc.DrawRectangle(null, Pen(Accent, 3), new Rect(-110, -30, 220, 60));
				Text(dc, "VERIFIED", 0, 13, 36, Accent, TextAlignment.Center, bold: true);
				dc.Pop(); dc.Pop(); dc.Pop(); dc.Pop();
			}
		}
		dc.Pop();
		if (!reduced)
		{
			double flash = Progress(t, 3.35, 3.38) - Progress(t, 3.38, 3.6);
			if (flash > 0)
			{
				dc.PushOpacity(flash * 0.32);
				Rect(dc, -2000, -2000, DesignWidth + 4000, DesignHeight + 4000, Ink);
				dc.Pop();
			}
		}
	}

	private void DrawWelcome(DrawingContext dc, double t, bool reduced)
	{
		if (t < 4.7) return;
		double wipe = reduced ? 1 : EaseOut(Progress(t, 4.72, 5.1));
		const string welcome = "WELCOME BACK";
		double size = 168;
		double width = TrackedWidth(welcome, size, 6, true);
		if (width > 1040) { size *= 1040 / width; width = TrackedWidth(welcome, size, 6, true); }
		dc.PushClip(new RectangleGeometry(new Rect(110, 290, 1400 * wipe, 210)));
		Tracked(dc, welcome, 120 + width / 2, 470, size, 6, Ink, true);
		dc.Pop();
		if (wipe < 1) Rect(dc, 110 + 1400 * wipe, 312, 6, 176, Accent);
		const string commander = "COMMANDER";
		int typed = reduced ? commander.Length : (int)Math.Floor(Progress(t, 5.0, 5.45) * commander.Length);
		if (typed > 0)
		{
			double cw = TrackedWidth(commander[..typed], 54, 26, true);
			Tracked(dc, commander[..typed], 124 + cw / 2, 556, 54, 26, Accent, true);
		}
		dc.PushOpacity(reduced ? 1 : EaseOut(Progress(t, 5.2, 5.5)));
		Line(dc, 1180, 220, 1480, 220, PanelLine);
		Text(dc, "GRAY WIND IS ONLINE", 1480, 250, 15, Ink, TextAlignment.Right, mono: true);
		Text(dc, "NANITE INTERFACE READY", 1480, 276, 13, Muted, TextAlignment.Right, mono: true);
		if (reduced || (int)Math.Floor(t * 3) % 2 == 0) Rect(dc, 1186, 238, 10, 10, Accent);
		dc.Pop();
		double prog = reduced ? 1 : EaseInOut(Progress(t, 4.75, 6.7));
		int pct = (int)Math.Floor(prog * 100 + 1e-9);
		dc.PushOpacity(reduced ? 1 : EaseOut(Progress(t, 4.75, 5.0)));
		Hazard(dc, 120, 640, 1360, 12, "#24282C", reduced ? 0 : -t * 80);
		Rect(dc, 120, 664, 1360, 10, "#24282C");
		Rect(dc, 120, 664, 1360 * prog, 10, pct >= 100 ? Accent : Ink);
		for (int i = 1; i < 10; i++) Rect(dc, 120 + i * 136, 676, 1, 8, Muted);
		Text(dc, pct.ToString("000", CultureInfo.InvariantCulture), 1420, 790, 128, Ink, TextAlignment.Right, bold: true);
		Text(dc, "%", 1426, 790, 40, Accent, bold: true);
		Text(dc, pct >= 100 ? "TERMINAL READY" : "LOADING DESKTOP RESOURCES", 120, 712, 14, pct >= 100 ? Accent : Secondary, mono: true);
		dc.Pop();
	}

	private static void Hazard(DrawingContext dc, double x, double y, double w, double h, string color, double offset)
	{
		dc.PushClip(new RectangleGeometry(new Rect(x, y, w, h)));
		double step = h * 1.6;
		double shift = ((offset % step) + step) % step;
		for (double i = -2; i < w / step + 2; i++)
		{
			double bx = x + i * step + shift;
			dc.DrawGeometry(Brush(color), null, Quad(new Point(bx, y + h), new Point(bx + h, y), new Point(bx + h + step / 2, y), new Point(bx + step / 2, y + h)));
		}
		dc.Pop();
	}

	private static void ArcStroke(DrawingContext dc, Point center, double r, double start, double end, string color, double width)
	{
		if (end - start <= 0.0001) return;
		Point a = new(center.X + Math.Cos(start) * r, center.Y + Math.Sin(start) * r);
		Point b = new(center.X + Math.Cos(end) * r, center.Y + Math.Sin(end) * r);
		StreamGeometry geometry = new();
		using (StreamGeometryContext context = geometry.Open())
		{
			context.BeginFigure(a, false, false);
			context.ArcTo(b, new Size(r, r), 0, end - start > Math.PI, SweepDirection.Clockwise, true, false);
		}
		geometry.Freeze();
		dc.DrawGeometry(null, Pen(color, width), geometry);
	}

	private static double EaseOut(double p) => 1 - Math.Pow(1 - p, 4);
	private static double EaseInOut(double p) => p < 0.5 ? 8 * Math.Pow(p, 4) : 1 - Math.Pow(-2 * p + 2, 4) / 2;

	private FormattedText Formatted(string value, double size, bool bold = false, bool mono = false, string color = Ink)
	{
		// Prevent unreadable auxiliary labels on smaller displays without changing layout.
		size = Math.Max(size, 11 / Math.Max(_scale, 0.1));
		var key = (value, size, bold, mono, color, _pixelsPerDip);
		if (!_textCache.TryGetValue(key, out FormattedText? text))
		{
			// The strip assembly draws the same glyphs repeatedly. Cache immutable
			// text runs rather than reshaping hundreds of letters every frame.
			if (_textCache.Count > 1500) _textCache.Clear();
			text = new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, mono ? Mono : bold ? SansBold : Sans, size, Brush(color), _pixelsPerDip);
			_textCache.Add(key, text);
		}
		return text;
	}

	private void Text(DrawingContext dc, string value, double x, double baseline, double size, string color, TextAlignment align = TextAlignment.Left, bool bold = false, bool mono = false)
	{
		if (value.Length == 0) return;
		FormattedText text = Formatted(value, size, bold, mono, color);
		double left = align == TextAlignment.Center ? x - text.WidthIncludingTrailingWhitespace / 2 : align == TextAlignment.Right ? x - text.WidthIncludingTrailingWhitespace : x;
		dc.DrawText(text, new Point(left, baseline - text.Baseline));
	}

	private double TrackedWidth(string value, double size, double tracking, bool bold = false)
	{
		double width = Math.Max(0, value.Length - 1) * tracking;
		foreach (char ch in value) width += Formatted(ch.ToString(), size, bold).WidthIncludingTrailingWhitespace;
		return width;
	}

	private void Tracked(DrawingContext dc, string value, double x, double baseline, double size, double tracking, string color, bool bold = false)
	{
		double left = x - TrackedWidth(value, size, tracking, bold) / 2;
		foreach (char ch in value)
		{
			FormattedText text = Formatted(ch.ToString(), size, bold, color: color);
			dc.DrawText(text, new Point(left, baseline - text.Baseline));
			left += text.WidthIncludingTrailingWhitespace + tracking;
		}
	}

	private static SolidColorBrush Brush(string color)
	{
		if (!BrushesByColor.TryGetValue(color, out SolidColorBrush? brush))
		{
			brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
			brush.Freeze(); BrushesByColor.Add(color, brush);
		}
		return brush;
	}

	private static Pen Pen(string color, double width)
	{
		string key = color + ":" + width.ToString(CultureInfo.InvariantCulture);
		if (!Pens.TryGetValue(key, out Pen? pen)) { pen = new Pen(Brush(color), width); pen.Freeze(); Pens.Add(key, pen); }
		return pen;
	}

	private static void Rect(DrawingContext dc, double x, double y, double w, double h, string color)
	{
		if (w > 0 && h > 0) dc.DrawRectangle(Brush(color), null, new Rect(x, y, w, h));
	}

	private static void Line(DrawingContext dc, double x1, double y1, double x2, double y2, string color, double width = 1) => dc.DrawLine(Pen(color, width), new Point(x1, y1), new Point(x2, y2));
	private static double Progress(double t, double start, double end) => Math.Clamp((t - start) / (end - start), 0, 1);
	private static double Ease(double p) => p * p * (3 - 2 * p);
	private static double Reveal(double t, double start, double end) => Ease(Progress(t, start, end));
	private static int PasswordCount(double t) => Math.Min(8, (int)Math.Floor(Progress(t, 1.5, 3.15) * 8 + 1e-8));

	private static void DrawPath(DrawingContext dc, Point[] points, string color, double width, double amount)
	{
		double total = 0;
		for (int i = 1; i < points.Length; i++) total += (points[i] - points[i - 1]).Length;
		double remain = total * Math.Clamp(amount, 0, 1);
		for (int i = 1; i < points.Length && remain > 0; i++)
		{
			Vector vector = points[i] - points[i - 1];
			double f = Math.Min(1, remain / vector.Length);
			dc.DrawLine(Pen(color, width), points[i - 1], points[i - 1] + vector * f);
			remain -= vector.Length;
		}
	}

	private static Point PointOnPath(Point[] points, double amount)
	{
		double total = 0;
		for (int i = 1; i < points.Length; i++) total += (points[i] - points[i - 1]).Length;
		double remain = total * Math.Clamp(amount, 0, 1);
		for (int i = 1; i < points.Length; i++)
		{
			Vector vector = points[i] - points[i - 1];
			if (remain <= vector.Length) return points[i - 1] + vector * (remain / vector.Length);
			remain -= vector.Length;
		}
		return points[^1];
	}

	private static void Arc(DrawingContext dc, Point center, double rx, double ry, double start, double end, string color)
	{
		Point a = new(center.X + Math.Cos(start) * rx, center.Y + Math.Sin(start) * ry);
		Point b = new(center.X + Math.Cos(end) * rx, center.Y + Math.Sin(end) * ry);
		StreamGeometry geometry = new();
		using (StreamGeometryContext context = geometry.Open())
		{
			context.BeginFigure(a, false, false);
			context.ArcTo(b, new Size(rx, ry), 0, false, SweepDirection.Clockwise, true, false);
		}
		geometry.Freeze();
		dc.DrawGeometry(null, Pen(color, 1.5), geometry);
	}

	private static LinearGradientBrush ScanGlow()
	{
		LinearGradientBrush brush = new();
		brush.StartPoint = new Point(0, 0); brush.EndPoint = new Point(0, 1);
		brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 94, 225, 245), 0));
		brush.GradientStops.Add(new GradientStop(Color.FromArgb(33, 94, 225, 245), 0.88));
		brush.GradientStops.Add(new GradientStop(Color.FromArgb(92, 213, 252, 255), 1));
		brush.Freeze(); return brush;
	}

	private static BitmapSource LoadLogo()
	{
		BitmapImage image = new();
		image.BeginInit();
		image.UriSource = new Uri("pack://application:,,,/Assets/rhodes-island.png", UriKind.Absolute);
		image.CacheOption = BitmapCacheOption.OnLoad;
		image.EndInit(); image.Freeze(); return image;
	}

	public static void ValidateAssetsAndTiming()
	{
		if (PasswordCount(1.5) != 0 || PasswordCount(1.8) != 1 || PasswordCount(2.4) != 4 || PasswordCount(3.2) != 8) throw new InvalidOperationException("PRTS password timing regression.");
		if (Math.Abs(TransitionStart - VoiceDuration - 0.122608) > 0.00001 || TransitionEnd <= TransitionStart || Duration <= TransitionEnd) throw new InvalidOperationException("PRTS transition timing regression.");
		double actual = ReadWaveDuration(Path.Combine(AppContext.BaseDirectory, "Assets", "startup-voice.wav"));
		if (Math.Abs(actual - VoiceDuration) > 0.002) throw new InvalidDataException("Startup voice differs from the approved recording.");
		BitmapSource logo = LoadLogo();
		if (logo.PixelWidth <= 0 || logo.PixelHeight <= 0) throw new InvalidDataException("Rhodes Island logo could not be decoded.");
	}

	private static double ReadWaveDuration(string path)
	{
		using FileStream stream = File.OpenRead(path);
		using BinaryReader reader = new(stream);
		if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException("Startup voice is not RIFF WAV.");
		reader.ReadUInt32();
		if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException("Startup voice has no WAVE header.");
		uint byteRate = 0, dataLength = 0;
		while (stream.Position + 8 <= stream.Length)
		{
			string name = new(reader.ReadChars(4));
			uint length = reader.ReadUInt32();
			long next = stream.Position + length + (length & 1);
			if (next > stream.Length) throw new InvalidDataException("Startup voice has a truncated WAV chunk.");
			if (name == "fmt " && length >= 16)
			{
				ushort format = reader.ReadUInt16(), channels = reader.ReadUInt16();
				uint rate = reader.ReadUInt32(); byteRate = reader.ReadUInt32();
				reader.ReadUInt16(); ushort bits = reader.ReadUInt16();
				if (format != 1 || channels != 1 || rate != 22050 || bits != 16) throw new InvalidDataException("Startup voice format differs from approved PCM recording.");
			}
			else if (name == "data") dataLength = length;
			stream.Position = next;
		}
		if (byteRate == 0 || dataLength == 0) throw new InvalidDataException("Startup voice is empty.");
		return (double)dataLength / byteRate;
	}

	public static void RenderTestFrames(string outputDirectory)
	{
		ValidateAssetsAndTiming();
		Directory.CreateDirectory(outputDirectory);
		(int Width, int Height, double Time, string Name)[] frames =
		[
			(1600,900,0.55,"01-ident"),(1600,900,1.1,"02-modules"),
			(1600,900,2.4,"03-password"),(1600,900,3.6,"04-online"),
			(1600,900,5.7,"05-welcome"),(1600,900,(TransitionStart + TransitionEnd) / 2,"06-real-ui-scan"),
			(1600,900,Duration,"07-transparent-complete"),(1366,768,2.4,"08-laptop"),
			(2560,1080,5.7,"09-ultrawide"),(600,900,2.4,"10-portrait")
		];
		foreach (var frame in frames)
		{
			using StartupBootOverlay overlay = new(AppContext.BaseDirectory);
			overlay._testTime = frame.Time;
			// This is an offscreen compositing test, not a replacement desktop asset.
			Grid host = new() { Width = frame.Width, Height = frame.Height, Background = Brush("#16313B") };
			host.Children.Add(new TextBlock { Text = "UNDERLYING UI · TRANSITION TEST", Foreground = Brush(Ink), FontSize = 30, Margin = new Thickness(40) });
			host.Children.Add(overlay);
			host.Measure(new Size(frame.Width, frame.Height));
			host.Arrange(new Rect(0, 0, frame.Width, frame.Height));
			host.UpdateLayout();
			RenderTargetBitmap bitmap = new(frame.Width, frame.Height, 96, 96, PixelFormats.Pbgra32);
			bitmap.Render(host);
			PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
			using FileStream output = File.Create(Path.Combine(outputDirectory, frame.Name + ".png"));
			encoder.Save(output);
		}
	}
}
