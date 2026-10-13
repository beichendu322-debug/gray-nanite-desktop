using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace StellarisUI;

internal enum CharacterTouchRegion { None, Ahoge, Body, Chest }

/// <summary>Sprite-space targets follow the Image's existing transforms, not screen coordinates.</summary>
internal sealed class CharacterHitMap
{
	private readonly CharacterHitGeometry _geometry;
	private readonly int _width;
	private readonly int _height;
	private readonly double _sourceWidth;
	private readonly double _sourceHeight;
	private readonly byte[] _pixels;

	public CharacterHitMap(BitmapSource source, CharacterHitGeometry? geometry = null)
	{
		_geometry = geometry ?? CharacterHitGeometry.Master;
		_width = source.PixelWidth;
		_height = source.PixelHeight;
		_sourceWidth = source.Width;
		_sourceHeight = source.Height;
		BitmapSource bitmap = source.Format == PixelFormats.Bgra32 ? source
			: new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
		_pixels = new byte[checked(_width * _height * 4)];
		bitmap.CopyPixels(_pixels, checked(_width * 4), 0);
	}

	public CharacterTouchRegion RegionAtImagePoint(Point point, Size imageSize)
		=> TryMapImagePoint(point, imageSize, out Point sourcePixel)
			? RegionAtSourcePixel(sourcePixel) : CharacterTouchRegion.None;

	public bool TryMapImagePoint(Point point, Size imageSize, out Point sourcePixel)
	{
		sourcePixel = default;
		if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)
			|| !double.IsFinite(imageSize.Width) || !double.IsFinite(imageSize.Height)
			|| imageSize.Width <= 0 || imageSize.Height <= 0
			|| point.X < 0 || point.Y < 0 || point.X >= imageSize.Width || point.Y >= imageSize.Height)
			return false;
		double scale = Math.Min(imageSize.Width / _sourceWidth, imageSize.Height / _sourceHeight);
		double offsetX = (imageSize.Width - _sourceWidth * scale) / 2;
		double offsetY = (imageSize.Height - _sourceHeight * scale) / 2;
		double x = (point.X - offsetX) / scale * _width / _sourceWidth;
		double y = (point.Y - offsetY) / scale * _height / _sourceHeight;
		if (x < 0 || y < 0 || x >= _width || y >= _height) return false;
		sourcePixel = new Point(x, y);
		return true;
	}

	public CharacterTouchRegion RegionAtSourcePixel(Point point)
	{
		if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)
			|| point.X < 0 || point.Y < 0 || point.X >= _width || point.Y >= _height)
			return CharacterTouchRegion.None;
		Point reference = new Point(point.X * 1024 / _width, point.Y * 1536 / _height);
		int x = (int)point.X;
		int y = (int)point.Y;
		if (_geometry.Ahoge.Contains(reference) && HasAhogeStrokeNearby(x, y))
			return CharacterTouchRegion.Ahoge;
		if (AlphaAt(x, y) < 64) return CharacterTouchRegion.None;
		return InsidePolygon(reference, _geometry.Chest)
			? CharacterTouchRegion.Chest : CharacterTouchRegion.Body;
	}

	private byte AlphaAt(int x, int y) => _pixels[(y * _width + x) * 4 + 3];

	private bool HasAhogeStrokeNearby(int x, int y)
	{
		double radiusX = 16.0 * _width / 1024;
		double radiusY = 16.0 * _height / 1536;
		for (int sy = Math.Max(0, y - (int)Math.Ceiling(radiusY)); sy <= Math.Min(_height - 1, y + (int)Math.Ceiling(radiusY)); sy++)
		{
			for (int sx = Math.Max(0, x - (int)Math.Ceiling(radiusX)); sx <= Math.Min(_width - 1, x + (int)Math.Ceiling(radiusX)); sx++)
			{
				double dx = (sx - x) / radiusX;
				double dy = (sy - y) / radiusY;
				if (dx * dx + dy * dy <= 1 && AlphaAt(sx, sy) >= 32
					&& _geometry.Ahoge.Contains(new Point(sx * 1024.0 / _width, sy * 1536.0 / _height)))
					return true;
			}
		}
		return false;
	}

	private static bool InsidePolygon(Point point, IReadOnlyList<Point> polygon)
	{
		bool inside = false;
		for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
		{
			Point a = polygon[i], b = polygon[j];
			if ((a.Y > point.Y) != (b.Y > point.Y)
				&& point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
				inside = !inside;
		}
		return inside;
	}
}

/// <summary>
/// A touch reply. <paramref name="Expression"/> is always a face that matches the line; when
/// <paramref name="Pose"/> is not Standard the whole-body pose sprite is shown instead.
/// </summary>
internal sealed record CharacterVoiceLine(CharacterTouchRegion Region, string FileName, string English, string Chinese,
	CharacterExpression Expression, CharacterPose Pose = CharacterPose.Standard)
{
	public CharacterLook Look => new CharacterLook(Expression, Pose).Normalized;
}

/// <summary>A spoken touch reaction overrides ambient mood until its own audio channel stops.</summary>
internal sealed class CharacterExpressionState
{
	public CharacterLook Ambient { get; private set; } = CharacterLook.Of(CharacterExpression.Neutral);
	public CharacterLook? Touch { get; private set; }
	public CharacterLook Current => Touch ?? Ambient;

	public void SetAmbientMood(string? mood) => Ambient = FromMood(mood);
	public void BeginTouch(CharacterLook look) => Touch = look.Normalized;
	public void BeginTouch(CharacterExpression expression) => BeginTouch(CharacterLook.Of(expression));
	public bool EndTouch()
	{
		if (Touch == null) return false;
		Touch = null;
		return true;
	}

	/// <summary>Ambient states: idle salute, happy smile, analyzing chin-on-hand, alert palm-out stop.</summary>
	public static CharacterLook FromMood(string? mood) => mood?.ToLowerInvariant() switch
	{
		"happy" => CharacterLook.Of(CharacterExpression.Smile),
		"analyzing" => new CharacterLook(CharacterExpression.Focused, CharacterPose.Thinking).Normalized,
		"alert" => new CharacterLook(CharacterExpression.Angry, CharacterPose.Stop).Normalized,
		_ => CharacterLook.Of(CharacterExpression.Neutral)
	};
}

/// <summary>Each group plays all three replies in random order, with no adjacent repeat between cycles.</summary>
internal sealed class CharacterReplyShuffle
{
	private readonly Random _random;
	private readonly Dictionary<CharacterTouchRegion, Queue<CharacterVoiceLine>> _bags = new();
	private readonly Dictionary<CharacterTouchRegion, string> _last = new();

	public CharacterReplyShuffle(Random? random = null) => _random = random ?? Random.Shared;

	public CharacterVoiceLine Next(CharacterTouchRegion region)
	{
		if (!_bags.TryGetValue(region, out Queue<CharacterVoiceLine>? bag) || bag.Count == 0)
		{
			CharacterVoiceLine[] lines = CharacterVoiceInteraction.Lines.Where(line => line.Region == region).ToArray();
			if (lines.Length == 0) throw new ArgumentOutOfRangeException(nameof(region));
			for (int i = lines.Length - 1; i > 0; i--)
			{
				int j = _random.Next(i + 1);
				(lines[i], lines[j]) = (lines[j], lines[i]);
			}
			if (_last.TryGetValue(region, out string? previous) && lines[0].FileName == previous && lines.Length > 1)
				(lines[0], lines[1]) = (lines[1], lines[0]);
			_bags[region] = bag = new Queue<CharacterVoiceLine>(lines);
		}
		CharacterVoiceLine selected = bag.Dequeue();
		_last[region] = selected.FileName;
		return selected;
	}
}

/// <summary>Independent local audio channel. Never controls QQ Music or changes the character's animation.</summary>
internal sealed class CharacterVoiceInteraction : IDisposable
{
	public static IReadOnlyList<CharacterVoiceLine> Lines { get; } = Array.AsReadOnly(new[]
	{
		new CharacterVoiceLine(CharacterTouchRegion.Ahoge, "ahoge-01.mp3", "Hey! That’s my antenna, Commander!", "喂！那是我的天线，指挥官！", CharacterExpression.Surprised, CharacterPose.AhogeGuard),
		new CharacterVoiceLine(CharacterTouchRegion.Ahoge, "ahoge-02.mp3", "Careful! You’re messing with my signal!", "小心点！我的信号都被你戳乱了！", CharacterExpression.Angry),
		new CharacterVoiceLine(CharacterTouchRegion.Ahoge, "ahoge-03.mp3", "Boop! …Wait. That was supposed to be my line.", "戳一下！……等等，这应该是我的台词才对。", CharacterExpression.Pout),
		new CharacterVoiceLine(CharacterTouchRegion.Body, "body-01.mp3", "Gray Wind reporting in! What’s the mission?", "灰风报到！有什么任务？", CharacterExpression.Laugh),
		new CharacterVoiceLine(CharacterTouchRegion.Body, "body-02.mp3", "Yes, Commander? You have my attention.", "怎么了，指挥官？我在听呢。", CharacterExpression.Smile, CharacterPose.Wave),
		new CharacterVoiceLine(CharacterTouchRegion.Body, "body-03.mp3", "Checking if I’m real? You could just ask.", "在确认我是不是真实的？直接问我就好了嘛。", CharacterExpression.Wry, CharacterPose.Hips),
		new CharacterVoiceLine(CharacterTouchRegion.Chest, "chest-01.mp3", "Commander… that is not the control panel.", "指挥官……那里可不是控制面板。", CharacterExpression.Shy, CharacterPose.Shy),
		new CharacterVoiceLine(CharacterTouchRegion.Chest, "chest-02.mp3", "Eyes up, Commander. I’m right here.", "看上面，指挥官。我在这里呢。", CharacterExpression.Angry, CharacterPose.ArmsCrossed),
		new CharacterVoiceLine(CharacterTouchRegion.Chest, "chest-03.mp3", "I’ll count that as a calibration test. Just this once.", "我就当你是在做校准测试了。仅此一次哦。", CharacterExpression.Wry)
	});

	private readonly string _baseDirectory;
	private readonly Action<CharacterVoiceLine> _onStarted;
	private readonly Action<string> _onFailed;
	private readonly Action? _onStopped;
	private readonly CharacterReplyShuffle _shuffle = new();
	private readonly DispatcherTimer _watchdog;
	private MediaPlayer? _player;
	private CharacterVoiceLine? _line;
	private bool _disposed;
	private long _deadline;
	private long _nextClick;

	public CharacterVoiceInteraction(string baseDirectory, Action<CharacterVoiceLine> onStarted, Action<string> onFailed,
		Action? onStopped = null)
	{
		_baseDirectory = baseDirectory;
		_onStarted = onStarted;
		_onFailed = onFailed;
		_onStopped = onStopped;
		_watchdog = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
		_watchdog.Tick += WatchdogTick;
	}

	public bool TryPlay(CharacterTouchRegion region)
	{
		if (_disposed || region == CharacterTouchRegion.None || _player != null || Environment.TickCount64 < _nextClick)
			return false;
		_line = _shuffle.Next(region);
		try
		{
			string path = Path.Combine(_baseDirectory, "Assets", "character-voice", _line.FileName);
			if (!File.Exists(path)) throw new FileNotFoundException("互动语音文件缺失", _line.FileName);
			_player = new MediaPlayer { Volume = 0.9, SpeedRatio = 1.0 };
			_player.MediaOpened += MediaOpened;
			_player.MediaEnded += MediaEnded;
			_player.MediaFailed += MediaFailed;
			_deadline = Environment.TickCount64 + 4000;
			_watchdog.Start();
			_player.Open(new Uri(path, UriKind.Absolute));
			return true;
		}
		catch (Exception ex)
		{
			Fail(ex.Message);
			return false;
		}
	}

	private void MediaOpened(object? sender, EventArgs args)
	{
		if (sender != _player || _player == null || _line == null || _disposed) return;
		try
		{
			double seconds = _player.NaturalDuration.HasTimeSpan ? _player.NaturalDuration.TimeSpan.TotalSeconds : 10;
			_deadline = Environment.TickCount64 + (long)(Math.Clamp(seconds, 1, 30) * 1000) + 2500;
			_player.Play();
			_onStarted(_line);
			App.LogStartup("Character voice playing; region=" + _line.Region + "; clip=" + _line.FileName
				+ "; expression=" + _line.Expression + "; look=" + _line.Look);
		}
		catch (Exception ex) { Fail(ex.Message); }
	}

	private void MediaEnded(object? sender, EventArgs args)
	{
		if (sender == _player) Stop();
	}

	private void MediaFailed(object? sender, ExceptionEventArgs args)
	{
		if (sender == _player) Fail(args.ErrorException.Message);
	}

	private void WatchdogTick(object? sender, EventArgs args)
	{
		if (_player != null && Environment.TickCount64 >= _deadline) Fail("语音加载或播放超时，请稍后再试。");
	}

	private void Fail(string error)
	{
		string clip = _line?.FileName ?? "unknown";
		Stop();
		App.LogStartup("Character voice failed; clip=" + clip + "; error=" + error);
		_onFailed("互动语音暂时无法播放：" + error);
	}

	public void Stop()
	{
		_watchdog.Stop();
		MediaPlayer? player = _player;
		_player = null;
		_line = null;
		_nextClick = Environment.TickCount64 + 350;
		if (player == null) return;
		player.MediaOpened -= MediaOpened;
		player.MediaEnded -= MediaEnded;
		player.MediaFailed -= MediaFailed;
		try { player.Close(); }
		catch (Exception ex) { App.LogStartup("Character voice close failed: " + ex.Message); }
		try { _onStopped?.Invoke(); }
		catch (Exception ex) { App.LogStartup("Character expression restore failed: " + ex.Message); }
	}

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
		Stop();
		_watchdog.Tick -= WatchdogTick;
	}

	public static void ValidateAssets(string baseDirectory)
	{
		CharacterTouchRegion[] groups = { CharacterTouchRegion.Ahoge, CharacterTouchRegion.Body, CharacterTouchRegion.Chest };
		if (Lines.Count != 9 || Lines.Select(line => line.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 9)
			throw new InvalidOperationException("Character voice catalog must have nine unique clips");
		foreach (CharacterTouchRegion region in groups)
		{
			if (Lines.Count(line => line.Region == region) != 3)
				throw new InvalidOperationException("Character voice region must have three replies: " + region);
			CharacterReplyShuffle shuffle = new(new Random(20261013));
			string? previous = null;
			for (int cycle = 0; cycle < 20; cycle++)
			{
				HashSet<string> seen = new(StringComparer.Ordinal);
				for (int index = 0; index < 3; index++)
				{
					CharacterVoiceLine selected = shuffle.Next(region);
					if (selected.Region != region || selected.FileName == previous || !seen.Add(selected.FileName))
						throw new InvalidOperationException("Character voice shuffle repeats or crosses regions");
					previous = selected.FileName;
				}
			}
		}
		Span<byte> header = stackalloc byte[3];
		foreach (CharacterVoiceLine line in Lines)
		{
			string path = Path.Combine(baseDirectory, "Assets", "character-voice", line.FileName);
			using FileStream file = File.OpenRead(path);
			if (file.Length < 1000) throw new InvalidOperationException("Character voice clip is incomplete: " + line.FileName);
			if (file.Read(header) != 3 || !(header.SequenceEqual("ID3"u8) || header[0] == 0xff && (header[1] & 0xe0) == 0xe0))
				throw new InvalidOperationException("Character voice clip has no MP3 header: " + line.FileName);
		}
	}
}
