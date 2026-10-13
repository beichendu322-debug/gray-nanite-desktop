using System;
using System.CodeDom.Compiler;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace StellarisUI;

public class App : Application
{
	private static Mutex? _instanceMutex;

	internal static void LogStartup(string message)
	{
		try
		{
			string dataDirectory = Path.Combine(AppContext.BaseDirectory, "data");
			Directory.CreateDirectory(dataDirectory);
			File.AppendAllText(Path.Combine(dataDirectory, "startup.log"), $"{DateTime.Now:O} PID={Environment.ProcessId} {message}{Environment.NewLine}");
		}
		catch
		{
		}
	}

	protected override void OnStartup(StartupEventArgs e)
	{
		LogStartup("OnStartup begin; args=" + string.Join(' ', e.Args));
		base.OnStartup(e);
		if (e.Args.Any((string a) => string.Equals(a, "--self-test", StringComparison.OrdinalIgnoreCase)))
		{
			Environment.ExitCode = StellarisUI.MainWindow.RunSelfTest();
			Shutdown();
			return;
		}
		string? desktopGateProbe = e.Args.FirstOrDefault(argument => argument.StartsWith("--desktop-gate-probe=", StringComparison.OrdinalIgnoreCase));
		if (desktopGateProbe != null)
		{
			// Diagnostic only: log once a second whether the intro would be allowed to start,
			// so lock/unlock detection can be checked without a normal (autostart-writing) run.
			int seconds = int.TryParse(desktopGateProbe[(desktopGateProbe.IndexOf('=') + 1)..], out int parsed) ? Math.Clamp(parsed, 1, 300) : 60;
			for (int i = 0; i < seconds; i++)
			{
				bool ready = StellarisUI.MainWindow.IsInteractiveDesktopReady(out string state);
				LogStartup("Desktop gate probe t=" + i + "s; ready=" + ready + "; " + state);
				System.Threading.Thread.Sleep(1000);
			}
			Shutdown();
			return;
		}
		string? introRenderTest = e.Args.FirstOrDefault(argument => argument.StartsWith("--intro-render-test=", StringComparison.OrdinalIgnoreCase));
		if (introRenderTest != null)
		{
			try
			{
				StartupBootOverlay.ValidateAssetsAndTiming();
				StartupBootOverlay.RenderTestFrames(introRenderTest[(introRenderTest.IndexOf('=') + 1)..]);
				LogStartup("Startup intro render test passed");
			}
			catch (Exception ex)
			{
				Environment.ExitCode = 1;
				LogStartup("Startup intro render test failed: " + ex);
			}
			Shutdown();
			return;
		}
		string? expressionRenderTest = e.Args.FirstOrDefault(argument => argument.StartsWith("--expression-render-test=", StringComparison.OrdinalIgnoreCase));
		if (expressionRenderTest != null)
		{
			try
			{
				string outputDirectory = expressionRenderTest[(expressionRenderTest.IndexOf('=') + 1)..];
				CharacterExpressionInteractionValidator.Validate();
				CharacterExpressionAssets.RenderPreview(outputDirectory);
				string? layoutSource = e.Args.FirstOrDefault(argument => argument.StartsWith("--character-layout-source=", StringComparison.OrdinalIgnoreCase));
				if (layoutSource != null)
				{
					CharacterExpressionAssets assets = new();
					foreach (CharacterLook look in CharacterExpressionAssets.AllLooks())
					{
						CharacterAssetValidator.RenderLayoutRegression(Path.Combine(outputDirectory, look.ToString()),
							layoutSource[(layoutSource.IndexOf('=') + 1)..], assets.Load(look), CharacterExpressionAssets.Geometry(look));
						LogStartup("Character sprite layout passed: " + look);
					}
				}
				LogStartup("Character expression render test passed; master, eight expressions, seven poses");
			}
			catch (Exception ex)
			{
				Environment.ExitCode = 1;
				LogStartup("Character expression render test failed: " + ex);
			}
			Shutdown();
			return;
		}
		string? seekTest = e.Args.FirstOrDefault(argument => argument.StartsWith("--qq-seek-test=", StringComparison.OrdinalIgnoreCase));
		string? characterRenderTest = e.Args.FirstOrDefault(argument => argument.StartsWith("--character-render-test=", StringComparison.OrdinalIgnoreCase));
		if (characterRenderTest != null)
		{
			try
			{
				CharacterAssetValidator.RenderPreview(characterRenderTest[(characterRenderTest.IndexOf('=') + 1)..]);
				string? layoutSource = e.Args.FirstOrDefault(argument => argument.StartsWith("--character-layout-source=", StringComparison.OrdinalIgnoreCase));
				if (layoutSource != null)
				{
					CharacterAssetValidator.RenderLayoutRegression(
						characterRenderTest[(characterRenderTest.IndexOf('=') + 1)..],
						layoutSource[(layoutSource.IndexOf('=') + 1)..]);
				}
				LogStartup("Character render test passed; approved anime sprite in all states");
			}
			catch (Exception ex)
			{
				Environment.ExitCode = 1;
				LogStartup("Character render test failed: " + ex);
			}
			Shutdown();
			return;
		}
		if (seekTest != null && double.TryParse(seekTest[(seekTest.IndexOf('=') + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out double seekSeconds))
		{
			Environment.ExitCode = QQMusicUiBridge.RunSeekTest(seekSeconds);
			Shutdown();
			return;
		}
		if (e.Args.Any(argument => string.Equals(argument, "--qq-play-collection-test", StringComparison.OrdinalIgnoreCase)))
		{
			Environment.ExitCode = QQMusicUiBridge.RunCollectionTest();
			Shutdown();
			return;
		}
		_instanceMutex = new Mutex(initiallyOwned: true, "StellarisUI.GrayNanite.Singleton", out var created);
		LogStartup("Singleton mutex created=" + created);
		if (!created)
		{
			Shutdown();
			return;
		}
		try
		{
			nint previousForeground = GetForegroundWindow();
			LogStartup("Constructing MainWindow");
			bool skipIntro = e.Args.Any(argument => string.Equals(argument, "--skip-intro", StringComparison.OrdinalIgnoreCase));
			MainWindow window = (MainWindow)(base.MainWindow = new MainWindow(skipIntro));
			LogStartup("MainWindow constructed");
			window.FitDesktopBounds();
			LogStartup("Desktop bounds applied");
			window.Show();
			window.Visibility = Visibility.Visible;
			LogStartup("MainWindow shown; handle=" + new System.Windows.Interop.WindowInteropHelper(window).Handle);
			window.RestorePreviousForeground(previousForeground);
			LogStartup("OnStartup complete");
		}
		catch (Exception ex)
		{
			LogStartup("Startup exception: " + ex);
			throw;
		}
	}

	[DllImport("user32.dll")]
	private static extern nint GetForegroundWindow();

	[STAThread]
	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "10.0.9.0")]
	public static void Main()
	{
		AppDomain.CurrentDomain.UnhandledException += (_, args) => LogStartup("Unhandled exception: " + args.ExceptionObject);
		TaskScheduler.UnobservedTaskException += (_, args) => LogStartup("Unobserved task exception: " + args.Exception);
		App app = new App();
		app.DispatcherUnhandledException += (_, args) => LogStartup("Dispatcher exception: " + args.Exception);
		app.Run();
		LogStartup("Application.Run returned");
	}
}
