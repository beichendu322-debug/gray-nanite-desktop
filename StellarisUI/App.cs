using System;
using System.CodeDom.Compiler;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace StellarisUI;

public class App : Application
{
	private static Mutex? _instanceMutex;

	protected override void OnStartup(StartupEventArgs e)
	{
		base.OnStartup(e);
		if (e.Args.Any((string a) => string.Equals(a, "--self-test", StringComparison.OrdinalIgnoreCase)))
		{
			Environment.ExitCode = StellarisUI.MainWindow.RunSelfTest();
			Shutdown();
			return;
		}
		_instanceMutex = new Mutex(initiallyOwned: true, "StellarisUI.GrayNanite.Singleton", out var created);
		if (!created)
		{
			Shutdown();
			return;
		}
		nint previousForeground = GetForegroundWindow();
		MainWindow window = (MainWindow)(base.MainWindow = new MainWindow());
		window.FitDesktopBounds();
		window.Show();
		window.Visibility = Visibility.Visible;
		window.RestorePreviousForeground(previousForeground);
	}

	[DllImport("user32.dll")]
	private static extern nint GetForegroundWindow();

	[STAThread]
	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "10.0.9.0")]
	public static void Main()
	{
		new App().Run();
	}
}
