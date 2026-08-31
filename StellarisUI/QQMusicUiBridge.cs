using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Automation;

namespace StellarisUI;

internal readonly record struct QQMusicUiStatus(bool Available, double Position, double Duration, bool Playing, string Title);

internal static class QQMusicUiBridge
{
	private readonly record struct Timeline(AutomationElement Root, nint WindowHandle, Rect StartTime, Rect EndTime, QQMusicUiStatus Status);

	public static QQMusicUiStatus ReadStatus()
	{
		return TryFindTimeline(out Timeline timeline) ? timeline.Status : default;
	}

	public static bool TrySeek(double seconds, double duration)
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
			List<(double Seconds, Rect Bounds)> clocks = new();
			foreach (AutomationElement element in textElements)
			{
				try
				{
					Rect bounds = element.Current.BoundingRectangle;
					if (bounds.Top >= window.Top + window.Height * 0.75 && TryParseClock(element.Current.Name, out double value))
					{
						clocks.Add((value, bounds));
					}
				}
				catch
				{
				}
			}
			foreach ((double position, Rect start) in clocks.OrderByDescending(clock => clock.Bounds.Top).ThenBy(clock => clock.Bounds.Left))
			{
				(double Seconds, Rect Bounds)? end = clocks
					.Where(clock => clock.Bounds.Left > start.Right && Math.Abs(clock.Bounds.Top - start.Top) < 18.0 && clock.Seconds > 0.0 && clock.Seconds >= position)
					.OrderBy(clock => clock.Bounds.Left)
					.Cast<(double Seconds, Rect Bounds)?>()
					.FirstOrDefault();
				if (end.HasValue)
				{
					bool playing = root.FindFirst(TreeScope.Descendants, new AndCondition(
						new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
						new PropertyCondition(AutomationElement.NameProperty, "暂停"))) != null;
					string title = root.Current.Name;
					timeline = new Timeline(root, windowHandle, start, end.Value.Bounds, new QQMusicUiStatus(true, position, end.Value.Seconds, playing, title));
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
		HashSet<uint> processIds = Process.GetProcessesByName("QQMusic").Select(process => (uint)process.Id).ToHashSet();
		nint best = IntPtr.Zero;
		long bestArea = 0;
		EnumWindows((window, _) =>
		{
			GetWindowThreadProcessId(window, out uint processId);
			if (processIds.Contains(processId) && IsWindowVisible(window) && GetWindowRect(window, out RECT rect))
			{
				long area = Math.Max(0, rect.Right - rect.Left) * (long)Math.Max(0, rect.Bottom - rect.Top);
				if (area > bestArea)
				{
					bestArea = area;
					best = window;
				}
			}
			return true;
		}, IntPtr.Zero);
		return best;
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

	[StructLayout(LayoutKind.Sequential)]
	private struct RECT
	{
		public int Left;
		public int Top;
		public int Right;
		public int Bottom;
	}

	private delegate bool EnumWindowsProc(nint window, nint parameter);

	private const uint MouseEventLeftDown = 0x0002;
	private const uint MouseEventLeftUp = 0x0004;

	[DllImport("user32.dll")]
	private static extern nint GetForegroundWindow();

	[DllImport("user32.dll")]
	private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

	[DllImport("user32.dll")]
	private static extern bool IsWindowVisible(nint window);

	[DllImport("user32.dll")]
	private static extern bool GetWindowRect(nint window, out RECT rect);

	[DllImport("user32.dll")]
	private static extern bool SetForegroundWindow(nint hWnd);

	[DllImport("user32.dll")]
	private static extern bool GetCursorPos(out POINT point);

	[DllImport("user32.dll")]
	private static extern bool SetCursorPos(int x, int y);

	[DllImport("user32.dll")]
	private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
}
