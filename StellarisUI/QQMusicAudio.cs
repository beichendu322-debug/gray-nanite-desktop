using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace StellarisUI;

internal static class QQMusicAudio
{
	private static readonly Guid AudioSessionManager2Guid = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
	private static readonly string[] ProcessNames = { "QQMusic", "QQMusicSvr" };

	public static double GetVolume()
	{
		ISimpleAudioVolume? volume = FindVolume();
		if (volume == null)
		{
			return -1.0;
		}
		try
		{
			volume.GetMasterVolume(out float level);
			return level * 100.0;
		}
		finally
		{
			Marshal.FinalReleaseComObject(volume);
		}
	}

	public static bool SetVolume(double percent)
	{
		ISimpleAudioVolume? volume = FindVolume();
		if (volume == null)
		{
			return false;
		}
		try
		{
			Guid context = Guid.Empty;
			volume.SetMasterVolume((float)Math.Clamp(percent / 100.0, 0.0, 1.0), ref context);
			return true;
		}
		finally
		{
			Marshal.FinalReleaseComObject(volume);
		}
	}

	private static ISimpleAudioVolume? FindVolume()
	{
		IMMDeviceEnumerator? enumerator = null;
		IMMDevice? device = null;
		IAudioSessionManager2? manager = null;
		IAudioSessionEnumerator? sessions = null;
		try
		{
			enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
			enumerator.GetDefaultAudioEndpoint(0, 1, out device);
			Guid iid = AudioSessionManager2Guid;
			device.Activate(ref iid, 23, IntPtr.Zero, out object managerObject);
			manager = (IAudioSessionManager2)managerObject;
			manager.GetSessionEnumerator(out sessions);
			sessions.GetCount(out int count);
			for (int index = 0; index < count; index++)
			{
				sessions.GetSession(index, out IAudioSessionControl control);
				bool retainControl = false;
				try
				{
					if (control is not IAudioSessionControl2 control2)
					{
						continue;
					}
					control2.GetProcessId(out uint processId);
					try
					{
						using Process process = Process.GetProcessById((int)processId);
						if (ProcessNames.Any(name => string.Equals(name, process.ProcessName, StringComparison.OrdinalIgnoreCase)))
						{
							ISimpleAudioVolume? result = control as ISimpleAudioVolume;
							if (result != null)
							{
								retainControl = true;
								return result;
							}
						}
					}
					catch
					{
					}
				}
				finally
				{
					if (!retainControl) Marshal.FinalReleaseComObject(control);
				}
			}
		}
		catch
		{
		}
		finally
		{
			if (sessions != null) Marshal.FinalReleaseComObject(sessions);
			if (manager != null) Marshal.FinalReleaseComObject(manager);
			if (device != null) Marshal.FinalReleaseComObject(device);
			if (enumerator != null) Marshal.FinalReleaseComObject(enumerator);
		}
		return null;
	}

	[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
	private class MMDeviceEnumerator { }

	[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
	private interface IMMDeviceEnumerator
	{
		int EnumAudioEndpoints(int dataFlow, int stateMask, out object devices);
		int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
	}

	[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("D666063F-1587-4E43-81F1-B948E807363F")]
	private interface IMMDevice
	{
		int Activate(ref Guid id, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
	}

	[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
	private interface IAudioSessionManager2
	{
		int GetAudioSessionControl(ref Guid sessionGuid, int streamFlags, out IAudioSessionControl control);
		int GetSimpleAudioVolume(ref Guid sessionGuid, int streamFlags, out ISimpleAudioVolume volume);
		int GetSessionEnumerator(out IAudioSessionEnumerator enumerator);
	}

	[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
	private interface IAudioSessionEnumerator
	{
		int GetCount(out int count);
		int GetSession(int index, out IAudioSessionControl session);
	}

	[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
	private interface IAudioSessionControl
	{
		int GetState(out int state);
		int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string value);
		int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid context);
		int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string value);
		int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid context);
		int GetGroupingParam(out Guid value);
		int SetGroupingParam(ref Guid value, ref Guid context);
		int RegisterAudioSessionNotification(IntPtr client);
		int UnregisterAudioSessionNotification(IntPtr client);
	}

	[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
	private interface IAudioSessionControl2 : IAudioSessionControl
	{
		new int GetState(out int state);
		new int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string value);
		new int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid context);
		new int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string value);
		new int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid context);
		new int GetGroupingParam(out Guid value);
		new int SetGroupingParam(ref Guid value, ref Guid context);
		new int RegisterAudioSessionNotification(IntPtr client);
		new int UnregisterAudioSessionNotification(IntPtr client);
		int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string value);
		int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string value);
		int GetProcessId(out uint processId);
	}

	[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
	private interface ISimpleAudioVolume
	{
		int SetMasterVolume(float level, ref Guid context);
		int GetMasterVolume(out float level);
		int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
		int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
	}
}
