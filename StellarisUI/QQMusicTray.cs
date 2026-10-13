using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace StellarisUI;

internal static class QQMusicTray
{
	private static readonly object StartupGate = new();
	private static long _startupVersion;
	private static nint _pendingHideWindow;

	// Take the ticket before launching: a later manual Open cancels the startup hide.
	public static long BeginStartup()
	{
		lock (StartupGate) return ++_startupVersion;
	}

	public static void CancelPendingHide()
	{
		lock (StartupGate)
		{
			++_startupVersion;
			// If the hide was already queued, queue a restore behind it on the same HWND.
			// A manual Open must win even in the short cross-process message interval.
			if (_pendingHideWindow != IntPtr.Zero) ShowWindowAsync(_pendingHideWindow, 4);
		}
	}

	internal static long VisibilityVersion
	{
		get { lock (StartupGate) return _startupVersion; }
	}

	public static async Task<bool> HideAfterStartupAsync(long startupTicket)
	{
		nint previousWindow = IntPtr.Zero;
		// Some versions replace the splash HWND with the player HWND during startup.
		// Wait for the same real player window twice, then hide it once only.
		for (int attempt = 0; attempt < 40; attempt++)
		{
			nint hiddenWindow = IntPtr.Zero;
			lock (StartupGate)
			{
				if (startupTicket != _startupVersion) return false;
				nint window = FindMainWindow(includeHidden: true);
				if (window != IntPtr.Zero && window == previousWindow)
				{
					// /background may already have placed the player in the tray.
					if (!IsWindowVisible(window)) return true;
					// SW_HIDE leaves QQ Music and its existing notification-area icon running.
					_pendingHideWindow = window;
					if (!ShowWindowAsync(window, 0))
					{
						_pendingHideWindow = IntPtr.Zero;
						return false;
					}
					hiddenWindow = window;
				}
				previousWindow = window;
			}
			if (hiddenWindow != IntPtr.Zero)
			{
				try
				{
					for (int confirmation = 0; confirmation < 8; confirmation++)
					{
						await Task.Delay(125).ConfigureAwait(false);
						lock (StartupGate)
						{
							if (startupTicket != _startupVersion) return false;
							if (!IsWindowVisible(hiddenWindow)) return true;
						}
					}
					return false;
				}
				finally
				{
					lock (StartupGate)
					{
						if (_pendingHideWindow == hiddenWindow) _pendingHideWindow = IntPtr.Zero;
					}
				}
			}
			await Task.Delay(250).ConfigureAwait(false);
		}
		return false;
	}

	internal static nint FindMainWindow(bool includeHidden)
	{
		HashSet<uint> processIds = new();
		foreach (Process process in Process.GetProcessesByName("QQMusic"))
		{
			using (process)
			{
				try { processIds.Add((uint)process.Id); }
				catch { }
			}
		}
		if (processIds.Count == 0) return IntPtr.Zero;
		nint best = IntPtr.Zero;
		long bestScore = 0;
		EnumWindows((window, _) =>
		{
			GetWindowThreadProcessId(window, out uint processId);
			if (!processIds.Contains(processId) || GetWindow(window, 4) != IntPtr.Zero ||
				(!includeHidden && !IsWindowVisible(window)) || !GetWindowRect(window, out RECT rect)) return true;
			int width = rect.Right - rect.Left;
			int height = rect.Bottom - rect.Top;
			if (IsIconic(window))
			{
				WINDOWPLACEMENT placement = new() { Length = (uint)Marshal.SizeOf<WINDOWPLACEMENT>() };
				if (GetWindowPlacement(window, ref placement))
				{
					width = placement.NormalPosition.Right - placement.NormalPosition.Left;
					height = placement.NormalPosition.Bottom - placement.NormalPosition.Top;
				}
			}
			if (width < 240 || height < 160) return true;
			StringBuilder title = new(256);
			GetWindowText(window, title, title.Capacity);
			if (title.Length == 0) return true;
			long area = (long)width * height;
			long score = area + (IsWindowVisible(window) ? 1L << 40 : 0);
			if (score > bestScore)
			{
				bestScore = score;
				best = window;
			}
			return true;
		}, IntPtr.Zero);
		return best;
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

	[StructLayout(LayoutKind.Sequential)]
	private struct WINDOWPLACEMENT
	{
		public uint Length;
		public uint Flags;
		public uint ShowCommand;
		public POINT MinPosition;
		public POINT MaxPosition;
		public RECT NormalPosition;
	}

	private delegate bool EnumWindowsProc(nint window, nint parameter);

	[DllImport("user32.dll")]
	private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

	[DllImport("user32.dll")]
	private static extern nint GetWindow(nint window, uint command);

	[DllImport("user32.dll")]
	private static extern bool IsWindowVisible(nint window);

	[DllImport("user32.dll")]
	private static extern bool IsIconic(nint window);

	[DllImport("user32.dll")]
	private static extern bool GetWindowPlacement(nint window, ref WINDOWPLACEMENT placement);

	[DllImport("user32.dll")]
	private static extern bool GetWindowRect(nint window, out RECT rect);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetWindowText(nint window, StringBuilder text, int count);

	[DllImport("user32.dll")]
	private static extern bool ShowWindowAsync(nint window, int command);
}
