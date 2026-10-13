using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Automation;

namespace StellarisUI;

internal readonly record struct QQMusicUiStatus(bool Available, double Position, double Duration, bool Playing, string Title);

internal static class QQMusicUiBridge
{
	private readonly record struct Timeline(AutomationElement Root, nint WindowHandle, AutomationElement StartClock, AutomationElement EndClock, Rect StartTime, Rect EndTime, QQMusicUiStatus Status);
	private static readonly object UiGate = new();
	private static Timeline? _lastTimeline;

	public static QQMusicUiStatus ReadStatus()
	{
		lock (UiGate)
		{
			// Read live clock names from the already identified UIA elements while hidden.
			// Polling must never reopen QQ Music just to refresh the desktop player.
			nint window = FindQQMusicWindow();
			if (window != IntPtr.Zero && !IsWindowVisible(window) && _lastTimeline is Timeline cached && cached.WindowHandle == window)
			{
				try
				{
					if (TryParseClock(cached.StartClock.Current.Name, out double position) && TryParseClock(cached.EndClock.Current.Name, out double duration) && duration > 0.0)
					{
						bool playing = cached.Root.FindFirst(TreeScope.Descendants, new AndCondition(
							new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
							new PropertyCondition(AutomationElement.NameProperty, "暂停"))) != null;
						return new QQMusicUiStatus(true, position, duration, playing, cached.Root.Current.Name);
					}
				}
				catch { }
			}
			return TryFindTimeline(out Timeline timeline) ? timeline.Status : default;
		}
	}

	public static bool TrySeek(double seconds, double duration)
	{
		lock (UiGate)
		{
			using TemporaryPlayerWindow visibleWindow = new(FindQQMusicWindow());
			return TrySeekVisible(seconds, duration);
		}
	}

	private static bool TrySeekVisible(double seconds, double duration)
	{
		if (!TryFindTimeline(out Timeline timeline) || duration <= 0.0)
		{
			return false;
		}
		double ratio = Math.Clamp(seconds / duration, 0.0, 1.0);
		double left = timeline.StartTime.Right + 8.0;
		double right = timeline.EndTime.Left - 8.0;
		double y = (timeline.StartTime.Top + timeline.StartTime.Bottom) / 2.0;
		if (right - left < 40.0)
		{
			return false;
		}

		AutomationElementCollection elements = timeline.Root.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition);
		foreach (AutomationElement element in elements)
		{
			try
			{
				Rect bounds = element.Current.BoundingRectangle;
				if (bounds.Left <= left && bounds.Right >= right && bounds.Top <= y && bounds.Bottom >= y && element.TryGetCurrentPattern(RangeValuePattern.Pattern, out object patternObject))
				{
					RangeValuePattern pattern = (RangeValuePattern)patternObject;
					if (!pattern.Current.IsReadOnly)
					{
						pattern.SetValue(pattern.Current.Minimum + ratio * (pattern.Current.Maximum - pattern.Current.Minimum));
						return true;
					}
				}
			}
			catch
			{
			}
		}

		// ponytail: QQ Music's Chromium timeline exposes labels but no RangeValuePattern;
		// use their live UIA bounds for one explicit seek click, and restore foreground/cursor immediately.
		return ClickScreenPoint(timeline.WindowHandle, left + ratio * (right - left), y);
	}

	public static bool TryPlayPause() => TryInvokeButton(bottomPlayer: true, "播放", "暂停");

	public static bool TryNext() => TryInvokeButton(bottomPlayer: true, "下一曲");

	public static bool TryPrevious() => TryInvokeButton(bottomPlayer: true, "上一曲");

	public static bool TryPlayCollection() => TryInvokeButton(bottomPlayer: false, "播放", "播放全部");

	public static int RunSeekTest(double seconds)
	{
		QQMusicUiStatus before = ReadStatus();
		if (!before.Available || !TrySeek(seconds, before.Duration))
		{
			Console.Error.WriteLine("QQ Music UI seek test could not locate a live timeline.");
			return 1;
		}
		Thread.Sleep(250);
		QQMusicUiStatus after = ReadStatus();
		Console.WriteLine($"QQ Music UI seek: {before.Position:0.0}s -> {after.Position:0.0}s / {after.Duration:0.0}s");
		return after.Available && Math.Abs(after.Position - Math.Clamp(seconds, 0.0, after.Duration)) <= 2.0 ? 0 : 1;
	}

	public static int RunCollectionTest()
	{
		if (!TryPlayCollection())
		{
			Console.Error.WriteLine("QQ Music collection play button was not available.");
			return 1;
		}
		Thread.Sleep(650);
		QQMusicUiStatus status = ReadStatus();
		Console.WriteLine($"QQ Music collection play: {(status.Playing ? "playing" : "not playing")} · {status.Title}");
		return status.Available && status.Playing ? 0 : 1;
	}

	private static bool TryFindTimeline(out Timeline timeline)
	{
		timeline = default;
		nint windowHandle = FindQQMusicWindow();
		if (windowHandle == IntPtr.Zero)
		{
			return false;
		}
		try
		{
			AutomationElement root = AutomationElement.FromHandle(windowHandle);
			Rect window = root.Current.BoundingRectangle;
			AutomationElementCollection textElements = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
			List<(double Seconds, Rect Bounds, AutomationElement Element)> clocks = new();
			foreach (AutomationElement element in textElements)
			{
				try
				{
					Rect bounds = element.Current.BoundingRectangle;
					if (bounds.Top >= window.Top + window.Height * 0.75 && TryParseClock(element.Current.Name, out double value))
					{
						clocks.Add((value, bounds, element));
					}
				}
				catch
				{
				}
			}
			foreach ((double position, Rect start, AutomationElement startClock) in clocks.OrderByDescending(clock => clock.Bounds.Top).ThenBy(clock => clock.Bounds.Left))
			{
				(double Seconds, Rect Bounds, AutomationElement Element)? end = clocks
					.Where(clock => clock.Bounds.Left > start.Right && Math.Abs(clock.Bounds.Top - start.Top) < 18.0 && clock.Seconds > 0.0 && clock.Seconds >= position)
					.OrderBy(clock => clock.Bounds.Left)
					.Cast<(double Seconds, Rect Bounds, AutomationElement Element)?>()
					.FirstOrDefault();
				if (end.HasValue)
				{
					bool playing = root.FindFirst(TreeScope.Descendants, new AndCondition(
						new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
						new PropertyCondition(AutomationElement.NameProperty, "暂停"))) != null;
					string title = root.Current.Name;
					timeline = new Timeline(root, windowHandle, startClock, end.Value.Element, start, end.Value.Bounds, new QQMusicUiStatus(true, position, end.Value.Seconds, playing, title));
					_lastTimeline = timeline;
					return true;
				}
			}
		}
		catch
		{
		}
		return false;
	}

	private static bool TryInvokeButton(bool bottomPlayer, params string[] names)
	{
		lock (UiGate)
		{
			using TemporaryPlayerWindow visibleWindow = new(FindQQMusicWindow());
			return TryInvokeVisibleButton(bottomPlayer, names);
		}
	}

	private static bool TryInvokeVisibleButton(bool bottomPlayer, params string[] names)
	{
		nint windowHandle = FindQQMusicWindow();
		if (windowHandle == IntPtr.Zero)
		{
			return false;
		}
		try
		{
			AutomationElement root = AutomationElement.FromHandle(windowHandle);
			Rect window = root.Current.BoundingRectangle;
			AutomationElementCollection buttons = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
			AutomationElement? button = buttons.Cast<AutomationElement>()
				.Where(candidate => names.Contains(candidate.Current.Name, StringComparer.Ordinal) && (bottomPlayer
					? candidate.Current.BoundingRectangle.Top > window.Top + window.Height * 0.72
					: candidate.Current.BoundingRectangle.Top > window.Top && candidate.Current.BoundingRectangle.Top < window.Top + window.Height * 0.72))
				.OrderBy(candidate => bottomPlayer ? -candidate.Current.BoundingRectangle.Top : candidate.Current.BoundingRectangle.Top)
				.FirstOrDefault();
			if (button == null)
			{
				return false;
			}
			if (button.TryGetCurrentPattern(InvokePattern.Pattern, out object patternObject))
			{
				((InvokePattern)patternObject).Invoke();
				return true;
			}
			Rect bounds = button.Current.BoundingRectangle;
			return ClickScreenPoint(windowHandle, bounds.Left + bounds.Width / 2.0, bounds.Top + bounds.Height / 2.0);
		}
		catch
		{
			return false;
		}
	}

	private static bool ClickScreenPoint(nint windowHandle, double x, double y)
	{
		nint previousForeground = GetForegroundWindow();
		GetCursorPos(out POINT previousCursor);
		try
		{
			SetForegroundWindow(windowHandle);
			Thread.Sleep(70);
			if (GetForegroundWindow() != windowHandle) return false;
			SetCursorPos((int)Math.Round(x), (int)Math.Round(y));
			mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
			mouse_event(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
			Thread.Sleep(90);
			return true;
		}
		finally
		{
			SetCursorPos(previousCursor.X, previousCursor.Y);
			if (previousForeground != IntPtr.Zero && previousForeground != windowHandle)
			{
				SetForegroundWindow(previousForeground);
			}
		}
	}

	private static nint FindQQMusicWindow()
	{
		return QQMusicTray.FindMainWindow(includeHidden: true);
	}

	private sealed class TemporaryPlayerWindow : IDisposable
	{
		private readonly nint _window;
		private readonly bool _wasVisible;
		private readonly bool _wasMinimized;
		private readonly long _visibilityVersion;
		private readonly nint _foreground;

		public TemporaryPlayerWindow(nint window)
		{
			_window = window;
			_wasVisible = window != IntPtr.Zero && IsWindowVisible(window);
			_wasMinimized = window != IntPtr.Zero && IsIconic(window);
			_visibilityVersion = QQMusicTray.VisibilityVersion;
			_foreground = GetForegroundWindow();
			if (window != IntPtr.Zero && (!_wasVisible || _wasMinimized))
			{
				ShowWindowAsync(window, _wasMinimized ? 9 : 4);
				Thread.Sleep(160);
			}
		}

		public void Dispose()
		{
			if (_window == IntPtr.Zero || _visibilityVersion != QQMusicTray.VisibilityVersion) return;
			if (!_wasVisible) ShowWindowAsync(_window, 0);
			else if (_wasMinimized) ShowWindowAsync(_window, 7);
			if (_foreground != IntPtr.Zero && _foreground != _window) SetForegroundWindow(_foreground);
		}
	}

	private static bool TryParseClock(string text, out double seconds)
	{
		seconds = 0.0;
		string[] parts = text.Split(':');
		if (parts.Length is < 2 or > 3 || parts.Any(part => !int.TryParse(part, out _)))
		{
			return false;
		}
		seconds = parts.Length == 2
			? int.Parse(parts[0]) * 60 + int.Parse(parts[1])
			: int.Parse(parts[0]) * 3600 + int.Parse(parts[1]) * 60 + int.Parse(parts[2]);
		return true;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct POINT
	{
		public int X;
		public int Y;
	}

	private const uint MouseEventLeftDown = 0x0002;
	private const uint MouseEventLeftUp = 0x0004;

	[DllImport("user32.dll")]
	private static extern nint GetForegroundWindow();

	[DllImport("user32.dll")]
	private static extern bool IsWindowVisible(nint window);

	[DllImport("user32.dll")]
	private static extern bool IsIconic(nint window);

	[DllImport("user32.dll")]
	private static extern bool ShowWindowAsync(nint window, int command);

	[DllImport("user32.dll")]
	private static extern bool SetForegroundWindow(nint hWnd);

	[DllImport("user32.dll")]
	private static extern bool GetCursorPos(out POINT point);

	[DllImport("user32.dll")]
	private static extern bool SetCursorPos(int x, int y);

	[DllImport("user32.dll")]
	private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
}
