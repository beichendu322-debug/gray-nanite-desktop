using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Microsoft.Win32;

namespace StellarisUI;

public partial class MainWindow : Window, IComponentConnector
{
	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct SHQUERYRBINFO
	{
		public uint cbSize;

		public long i64Size;

		public long i64NumItems;
	}

	private delegate void WinEventDelegate(nint hook, uint eventType, nint hwnd, int objectId, int childId, uint eventThreadId, uint eventTime);

	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
	{
		WriteIndented = true,
		PropertyNameCaseInsensitive = true
	};

	private readonly string _baseDirectory = AppContext.BaseDirectory;

	private readonly string _projectDirectory;

	private readonly string _dataDirectory = Path.Combine(AppContext.BaseDirectory, "data");

	private readonly ObservableCollection<ChatLine> _chatLines = new ObservableCollection<ChatLine>();

	private readonly List<FileItem> _allItems = new List<FileItem>();

	private AppConfig _config;

	private FolderBinding? _activeBinding;

	private FileSystemWatcher? _watcher;

	private DispatcherTimer? _clockTimer;

	private DispatcherTimer? _weatherTimer;

	private DispatcherTimer? _blinkTimer;

	private DispatcherTimer? _glitchTimer;

	private DispatcherTimer? _musicTimer;

	private DispatcherTimer? _musicVolumeTimer;

	private readonly HttpClient _weatherClient = CreateWeatherClient();

	private bool _weatherRefreshInProgress;

	private Process? _codexProcess;

	private CancellationTokenSource? _codexCancellation;

	private string? _codexSessionId;

	private string _activePath = string.Empty;

	private HwndSource? _source;

	private System.Windows.Controls.Button? _moveSelectionButton;

	private System.Windows.Controls.ContextMenu? _moveMenu;

	private System.Windows.Controls.ContextMenu? _driveMenu;

	private nint _windowHandle;

	private bool _closing;

	private bool _textEntryDialogOpen;

	private string _characterMood = string.Empty;

	private bool _characterAnimationStarted;

	private int _blinkCycle;

	private int _glitchCycle;

	private int _musicTick;

	private readonly SemaphoreSlim _musicBridgeGate = new SemaphoreSlim(1, 1);

	private bool _musicRefreshInProgress;

	private bool _musicSeeking;

	private bool _musicPlaying;

	private bool _musicSessionAvailable;

	private bool _musicUiReady;

	private double _musicPositionSeconds;

	private double _musicDurationSeconds;

	private int _musicModeIndex;

	private static readonly string[] MusicModeKeys = { "sequence", "repeat-one", "repeat-all", "shuffle" };

	private static readonly string[] MusicModeLabels = { "顺序播放", "单曲循环", "列表循环", "随机播放" };

	private NotifyIcon? _trayIcon;

	private nint _foregroundHook;

	private WinEventDelegate? _winEventDelegate;

	private const int HotkeyId = 18960;

	private const int WmHotkey = 786;

	private const int WmMouseActivate = 33;

	private const int MaNoActivate = 3;

	private const uint ModNoRepeat = 16384u;

	private const uint VkF10 = 121u;

	private const string ResponseSchema = """
	{
	  "type": "object",
	  "properties": {
	    "reply": {"type": "string"},
	    "mood": {"type": "string", "enum": ["idle", "happy", "analyzing", "alert"]},
	    "status": {"type": "string"},
	    "actions": {
	      "type": "array",
	      "items": {
	        "type": "object",
	        "properties": {
	          "type": {"type": "string", "enum": ["create_folder", "create_docx", "create_text_file", "open_item"]},
	          "path": {"type": "string"},
	          "content": {"type": ["string", "null"]}
	        },
	        "required": ["type", "path", "content"],
	        "additionalProperties": false
	      }
	    }
	  },
	  "required": ["reply", "mood", "status", "actions"],
	  "additionalProperties": false
	}
	""";

	private const uint SwpNoSize = 1u;

	private const uint SwpNoMove = 2u;

	private const uint SwpNoActivate = 16u;

	private const uint SwpShowWindow = 64u;

	private const uint EventSystemForeground = 3u;

	private const uint WinEventOutOfContext = 0u;

	private const uint WinEventSkipOwnProcess = 2u;

	public MainWindow()
	{
		InitializeComponent();
		RemoveUnneededHud();
		FileList.SelectionMode = System.Windows.Controls.SelectionMode.Extended;
		_projectDirectory = FindProjectDirectory(_baseDirectory);
		LoadCharacterMood("idle");
		CommandBackdrop.Source = LoadImageAsset("background-command.png");
		InitializeTrayIcon();
		_config = LoadConfig();
		SetStartup(_config.StartWithWindows);
		MusicVolumeSlider.Value = Math.Clamp(_config.QQMusicVolume, 0.0, 100.0);
		MusicArtistText.Text = string.IsNullOrWhiteSpace(_config.QQMusicPlaylistName) ? "开机续播 · 上次播放队列" : "开机续播 · " + _config.QQMusicPlaylistName;
		_musicUiReady = true;
		InitializeDriveSelection();
		AddMoveSelectionButton();
		_codexSessionId = _config.CodexSessionId;
		ChatHistory.ItemsSource = _chatLines;
		LoadChatHistory();
		BuildFolderButtons();
		SelectFolder("inbox");
		UpdateSystemStatus();
		_clockTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromSeconds(1L)
		};
		_clockTimer.Tick += delegate
		{
			UpdateSystemStatus();
			AnchorDesktopLayer();
		};
		_clockTimer.Start();
		_weatherTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromMinutes(15.0)
		};
		_weatherTimer.Tick += async delegate
		{
			await RefreshWeatherAsync();
		};
		_musicTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromSeconds(1.0)
		};
		_musicTimer.Tick += async delegate
		{
			_musicTick++;
			if (_musicPlaying && !_musicSeeking && _musicDurationSeconds > 0.0)
			{
				_musicPositionSeconds = Math.Min(_musicDurationSeconds, _musicPositionSeconds + 1.0);
				UpdateMusicProgressUi();
			}
			int refreshInterval = _musicSessionAvailable ? 3 : 9;
			if (_musicTick % refreshInterval == 0)
			{
				await RefreshMusicStatusAsync();
			}
		};
		_musicVolumeTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(280.0)
		};
		_musicVolumeTimer.Tick += async delegate
		{
			_musicVolumeTimer?.Stop();
			double volume = MusicVolumeSlider.Value;
			_config.QQMusicVolume = volume;
			SaveConfig();
			await Task.Run(() => QQMusicAudio.SetVolume(volume));
		};
		base.Loaded += async delegate
		{
			StartCharacterAnimation();
			await RefreshWeatherAsync();
			_weatherTimer?.Start();
			await InitializeMusicAsync();
			_musicTimer?.Start();
		};
		base.SourceInitialized += delegate
		{
			RegisterHotkey();
		};
		base.PreviewKeyDown += Window_PreviewKeyDown;
		base.Activated += delegate
		{
			ScheduleDesktopLayerSync();
		};
		base.Deactivated += delegate
		{
			ScheduleDesktopLayerSync();
		};
		base.Closed += delegate
		{
			DisposeResources();
		};
	}

	public static int RunSelfTest()
	{
		try
		{
			using (JsonDocument responseSchema = JsonDocument.Parse(ResponseSchema))
			{
				JsonElement actionTypes = responseSchema.RootElement.GetProperty("properties").GetProperty("actions").GetProperty("items").GetProperty("properties").GetProperty("type").GetProperty("enum");
				if (!actionTypes.EnumerateArray().Any((JsonElement item) => string.Equals(item.GetString(), "open_item", StringComparison.Ordinal)))
				{
					throw new InvalidOperationException("open-item action is missing from the response schema");
				}
			}
			System.Resources.ResourceManager embeddedResources = new System.Resources.ResourceManager("StellarisUI.g", typeof(MainWindow).Assembly);
			using Stream embeddedCharacter = embeddedResources.GetStream("assets/character-neutral.png", CultureInfo.InvariantCulture)
				?? throw new InvalidOperationException("embedded character resource is missing");
			if (embeddedCharacter.Length < 100000)
			{
				throw new InvalidOperationException("embedded character resource is incomplete");
			}
			embeddedResources.ReleaseAllResources();
			string musicBridge = Path.Combine(AppContext.BaseDirectory, "Tools", "QQMusicBridge.ps1");
			if (!File.Exists(musicBridge) || !File.ReadAllText(musicBridge, Encoding.UTF8).Contains("TryChangePlaybackPositionAsync", StringComparison.Ordinal))
			{
				throw new InvalidOperationException("QQ Music bridge is missing or incomplete");
			}
			AppConfig config = AppConfig.CreateDefault(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
			if (config.ConfigVersion != 11 || !config.StartWithWindows || !config.QQMusicAutoStart || !config.QQMusicAutoPlay || config.QQMusicVolume <= 0.0 || config.Folders.Count != 8 || config.Folders.Any((FolderBinding f) => string.IsNullOrWhiteSpace(f.TerminalName)) || config.Folders.All((FolderBinding f) => f.Id != "inbox"))
			{
				throw new InvalidOperationException("default folder bindings are incomplete");
			}
			if ((JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config, JsonOptions), JsonOptions) ?? throw new InvalidOperationException("config round-trip failed")).Folders.Count != 8)
			{
				throw new InvalidOperationException("config round-trip lost bindings");
			}
			AppConfig legacy = AppConfig.CreateDefault("D:\\Desktop");
			legacy.ConfigVersion = 9;
			legacy.StartWithWindows = false;
			legacy.Folders.RemoveAll((FolderBinding folder) => folder.Id == "inbox");
			if (!UpgradeConfig(legacy, "D:\\Desktop") || legacy.ConfigVersion != 11 || !legacy.StartWithWindows || !legacy.QQMusicAutoStart || !legacy.QQMusicAutoPlay || legacy.Folders.Count != 8)
			{
				throw new InvalidOperationException("legacy startup migration failed");
			}
			legacy.StartWithWindows = false;
			if (UpgradeConfig(legacy, "D:\\Desktop") || legacy.StartWithWindows)
			{
				throw new InvalidOperationException("startup preference was forced after migration");
			}
			if (!IsAllowedPath(Path.Combine("D:\\project", "nested"), new[] { "D:\\project" }))
			{
				throw new InvalidOperationException("allowed path check failed");
			}
			if (IsAllowedPath("C:\\other", new[] { "D:\\project" }))
			{
				throw new InvalidOperationException("allowed path check is too broad");
			}
			List<FileItem> source = new List<FileItem>
			{
				new FileItem { Name = "first.docx", TypeLabel = "DOCX 文件" }
			};
			List<FileItem> firstSnapshot = CreateFilteredSnapshot(source, string.Empty);
			source.Clear();
			source.Add(new FileItem { Name = "second.pdf", TypeLabel = "PDF 文件" });
			List<FileItem> secondSnapshot = CreateFilteredSnapshot(source, string.Empty);
			if (firstSnapshot.Count != 1 || firstSnapshot[0].Name != "first.docx" || secondSnapshot.Count != 1 || secondSnapshot[0].Name != "second.pdf")
			{
				throw new InvalidOperationException("directory switching snapshot regression");
			}
			string testDocument = Path.Combine(Path.GetTempPath(), $"gray-nanite-{Guid.NewGuid():N}.docx");
			try
			{
				CreateMinimalDocx(testDocument, "测试文档");
				using ZipArchive archive = ZipFile.OpenRead(testDocument);
				if (archive.GetEntry("[Content_Types].xml") == null || archive.GetEntry("_rels/.rels") == null || archive.GetEntry("word/document.xml") == null)
				{
					throw new InvalidOperationException("generated Word document is incomplete");
				}
			}
			finally
			{
				if (File.Exists(testDocument))
				{
					File.Delete(testDocument);
				}
			}
			using (JsonDocument weather = JsonDocument.Parse("{\"data\":{\"current_condition\":[{\"temp_C\":\"18\",\"weatherCode\":\"113\",\"weatherDesc\":[{\"value\":\"晴\"}]}],\"nearest_area\":[{\"areaName\":[{\"value\":\"测试市\"}]}]}}"))
			{
				JsonElement payload = weather.RootElement.GetProperty("data");
				if (!TryGetFirstObject(payload, "current_condition", out JsonElement current) || ReadScalar(current, "temp_C") != "18" || ReadWeatherValue(current, "weatherDesc") != "晴" || SelectWeatherGlyph("晴", "113") != "☀")
				{
					throw new InvalidOperationException("weather response parser regression");
				}
			}
			Console.WriteLine("StellarisUI self-test passed.");
			return 0;
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine("StellarisUI self-test failed: " + ex.Message);
			return 1;
		}
	}

	private AppConfig LoadConfig()
	{
		Directory.CreateDirectory(_dataDirectory);
		string path = Path.Combine(_dataDirectory, "config.json");
		try
		{
			if (File.Exists(path))
			{
				AppConfig existing = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), JsonOptions);
				List<FolderBinding> list = existing?.Folders;
				if (list != null && list.Count > 0)
				{
					if (UpgradeConfig(existing!, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)))
					{
						File.WriteAllText(path, JsonSerializer.Serialize(existing, JsonOptions), Encoding.UTF8);
					}
					return existing;
				}
			}
		}
		catch
		{
		}
		AppConfig created = AppConfig.CreateDefault(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
		SaveConfig(created);
		return created;
	}

	private static bool UpgradeConfig(AppConfig config, string desktop)
	{
		bool upgraded = false;
		if (config.ConfigVersion < 10)
		{
			config.ConfigVersion = 10;
			config.StartWithWindows = true;
			upgraded = true;
		}
		if (config.ConfigVersion < 11)
		{
			config.ConfigVersion = 11;
			config.QQMusicAutoStart = true;
			config.QQMusicAutoPlay = true;
			config.QQMusicPlaylistName = "上次播放队列";
			config.QQMusicVolume = 62.0;
			upgraded = true;
		}
		foreach (FolderBinding fallback in AppConfig.CreateDefault(desktop).Folders)
		{
			if (!config.Folders.Any((FolderBinding binding) => string.Equals(binding.Id, fallback.Id, StringComparison.OrdinalIgnoreCase)))
			{
				config.Folders.Add(fallback);
				upgraded = true;
			}
		}
		return upgraded;
	}

	private void SaveConfig(AppConfig? value = null)
	{
		_config = value ?? _config;
		Directory.CreateDirectory(_dataDirectory);
		File.WriteAllText(Path.Combine(_dataDirectory, "config.json"), JsonSerializer.Serialize(_config, JsonOptions), Encoding.UTF8);
	}

	private void BuildFolderButtons()
	{
		FolderButtonsPanel.Children.Clear();
		foreach (FolderBinding binding in _config.Folders.Take(8))
		{
			Grid grid = new Grid();
			grid.ColumnDefinitions.Add(new ColumnDefinition
			{
				Width = new GridLength(32.0)
			});
			grid.ColumnDefinitions.Add(new ColumnDefinition
			{
				Width = new GridLength(1.0, GridUnitType.Star)
			});
			grid.ColumnDefinitions.Add(new ColumnDefinition
			{
				Width = GridLength.Auto
			});
			grid.Children.Add(new TextBlock
			{
				Text = binding.Code,
				FontFamily = new System.Windows.Media.FontFamily("Consolas"),
				FontSize = 16.0,
				FontWeight = FontWeights.Bold,
				Foreground = (FindResource("Cyan") as System.Windows.Media.Brush),
				VerticalAlignment = VerticalAlignment.Center
			});
			StackPanel labels = new StackPanel
			{
				Margin = new Thickness(9.0, 0.0, 0.0, 0.0)
			};
			labels.Children.Add(new TextBlock
			{
				Text = binding.TerminalName,
				FontSize = 18.0,
				FontWeight = FontWeights.Bold,
				Foreground = (FindResource("Silver") as System.Windows.Media.Brush)
			});
			labels.Children.Add(new TextBlock
			{
				Text = Directory.Exists(binding.Path) ? "●  ONLINE" : "○  UNBOUND",
				FontFamily = new System.Windows.Media.FontFamily("Consolas"),
				FontSize = 10.0,
				FontWeight = FontWeights.SemiBold,
				Foreground = (FindResource(Directory.Exists(binding.Path) ? "Good" : "Gold") as System.Windows.Media.Brush),
				Margin = new Thickness(0.0, 3.0, 0.0, 0.0)
			});
			Grid.SetColumn(labels, 1);
			grid.Children.Add(labels);
			TextBlock arrow = new TextBlock
			{
				Text = "›",
				FontSize = 20.0,
				Foreground = (FindResource("Muted") as System.Windows.Media.Brush),
				VerticalAlignment = VerticalAlignment.Center
			};
			Grid.SetColumn(arrow, 2);
			grid.Children.Add(arrow);
			System.Windows.Controls.Button button = new System.Windows.Controls.Button
			{
				Style = (FindResource("FolderButton") as Style),
				Content = grid,
				Tag = binding.Id,
				ToolTip = binding.Path
			};
			button.Click += FolderButton_Click;
			FolderButtonsPanel.Children.Add(button);
		}
		int valid = _config.Folders.Count((FolderBinding f) => Directory.Exists(f.Path));
		BindingStatusText.Text = ((valid == _config.Folders.Count) ? $"{valid}/{_config.Folders.Count} 个目录在线" : $"{valid}/{_config.Folders.Count} 个目录在线 · 点击 BIND FOLDERS");
	}

	private void FolderButton_Click(object sender, RoutedEventArgs e)
	{
		SelectFolder((string)((System.Windows.Controls.Button)sender).Tag);
	}

	private void UpdateFolderButtonHighlight(string id)
	{
		foreach (System.Windows.Controls.Button button in FolderButtonsPanel.Children.OfType<System.Windows.Controls.Button>())
		{
			bool selected = string.Equals(button.Tag as string, id, StringComparison.OrdinalIgnoreCase);
			if (selected)
			{
				button.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(216, 28, 53, 66));
				button.BorderBrush = FindResource("Cyan") as System.Windows.Media.Brush;
				button.FontWeight = FontWeights.SemiBold;
			}
			else
			{
				button.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
				button.ClearValue(System.Windows.Controls.Control.BorderBrushProperty);
				button.ClearValue(System.Windows.Controls.Control.FontWeightProperty);
			}
		}
	}

	private void SelectFolder(string id)
	{
		_activeBinding = _config.Folders.FirstOrDefault((FolderBinding f) => f.Id == id) ?? _config.Folders.Last();
		UpdateFolderButtonHighlight(_activeBinding.Id);
		ActiveFolderText.Text = _activeBinding.TerminalName + "  /  " + _activeBinding.SourceName;
		_activePath = _activeBinding.Path;
		ActivePathText.Text = (string.IsNullOrWhiteSpace(_activePath) ? "未绑定路径" : _activePath);
		DisposeWatcher();
		if (Directory.Exists(_activePath))
		{
			ActiveStateText.Text = "ONLINE";
			ActiveStateText.Foreground = FindResource("Good") as System.Windows.Media.Brush;
			LoadDirectory(_activePath);
			TryWatch(_activePath);
		}
		else
		{
			ActiveStateText.Text = "UNBOUND";
			ActiveStateText.Foreground = FindResource("Gold") as System.Windows.Media.Brush;
			_allItems.Clear();
			ApplyFileFilter();
			FooterStatusText.Text = "LOG // DIRECTORY NOT BOUND";
		}
		UpdateMoveSelectionVisibility();
	}

	private void LoadDirectory(string path)
	{
		_allItems.Clear();
		try
		{
			foreach (FileSystemInfo entry in (from e in new DirectoryInfo(path).EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly)
				orderby !(e is DirectoryInfo)
				select e).ThenBy<FileSystemInfo, string>((FileSystemInfo e) => e.Name, StringComparer.CurrentCultureIgnoreCase))
			{
				bool directory = entry is DirectoryInfo;
				List<FileItem> allItems = _allItems;
				FileItem obj = new FileItem
				{
					Name = entry.Name,
					FullPath = entry.FullName,
					IsDirectory = directory
				};
				object badge;
				if (!directory)
				{
					string ext = Path.GetExtension(entry.Name).TrimStart('.').ToUpperInvariant();
					badge = ((ext != null && ext.Length > 0) ? ext.Substring(0, Math.Min(4, ext.Length)) : "FILE");
				}
				else
				{
					badge = "DIR";
				}
				obj.Badge = (string)badge;
				obj.TypeLabel = (directory ? "文件夹" : (string.IsNullOrWhiteSpace(Path.GetExtension(entry.Name)) ? "文件" : (Path.GetExtension(entry.Name).TrimStart('.').ToUpperInvariant() + " 文件")));
				obj.ModifiedLabel = entry.LastWriteTime.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
				allItems.Add(obj);
			}
			FooterStatusText.Text = $"LOG // SCANNED {_allItems.Count:000} ITEMS";
		}
		catch (UnauthorizedAccessException)
		{
			FooterStatusText.Text = "LOG // ACCESS DENIED";
		}
		catch (IOException)
		{
			FooterStatusText.Text = "LOG // DIRECTORY UNAVAILABLE";
		}
		ApplyFileFilter();
		UpdateSystemStatus();
	}

	private void ApplyFileFilter()
	{
		string query = SearchBox?.Text?.Trim() ?? string.Empty;
		List<FileItem> filtered = CreateFilteredSnapshot(_allItems, query);
		FileList.ItemsSource = filtered;
		EmptyState.Visibility = ((filtered.Count != 0) ? Visibility.Collapsed : Visibility.Visible);
	}

	private static List<FileItem> CreateFilteredSnapshot(IEnumerable<FileItem> items, string query)
	{
		return (string.IsNullOrEmpty(query) ? items : items.Where((FileItem i) => i.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) || i.TypeLabel.Contains(query, StringComparison.CurrentCultureIgnoreCase))).ToList();
	}

	private void TryWatch(string path)
	{
		try
		{
			_watcher = new FileSystemWatcher(path)
			{
				IncludeSubdirectories = false,
				NotifyFilter = (NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite)
			};
			_watcher.Created += FileChanged;
			_watcher.Deleted += FileChanged;
			_watcher.Renamed += FileChanged;
			_watcher.Changed += FileChanged;
			_watcher.EnableRaisingEvents = true;
		}
		catch
		{
			_watcher = null;
		}
	}

	private void FileChanged(object? sender, FileSystemEventArgs e)
	{
		base.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
		{
			if (!_closing && Directory.Exists(_activePath))
			{
				LoadDirectory(_activePath);
			}
		});
	}

	private void DisposeWatcher()
	{
		if (_watcher != null)
		{
			_watcher.EnableRaisingEvents = false;
			_watcher.Dispose();
			_watcher = null;
		}
	}

	private void UpdateSystemStatus()
	{
		DateTime now = DateTime.Now;
		ClockText.Text = now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
		DateText.Text = now.ToString("yyyy.MM.dd ddd", CultureInfo.InvariantCulture).ToUpperInvariant();
		int count = _config.Folders.Sum((FolderBinding f) => SafeCount(f.Path));
		TotalCountText.Text = Math.Min(count, 999).ToString("000", CultureInfo.InvariantCulture);
		DriveInfo drive = ResolveStorageDrive();
		StorageText.Text = ((drive == null) ? "--" : $"{drive.RootDirectory.FullName.TrimEnd('\\')} {(double)drive.AvailableFreeSpace / 1024.0 / 1024.0 / 1024.0:0.0}G");
		PowerStatus power = SystemInformation.PowerStatus;
		int pct = Math.Clamp((int)Math.Round(power.BatteryLifePercent * 100f), 0, 100);
		BatteryText.Text = ((power.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online) ? $"POWER // AC {pct}%" : $"POWER // {pct}%");
		NetworkText.Text = "NETWORK // LOCAL";
		RecycleText.Text = GetRecycleText();
	}

	private static HttpClient CreateWeatherClient()
	{
		HttpClient client = new HttpClient
		{
			Timeout = TimeSpan.FromSeconds(10.0)
		};
		client.DefaultRequestHeaders.UserAgent.ParseAdd("GrayNaniteDesktop/1.0");
		return client;
	}

	private async Task RefreshWeatherAsync()
	{
		if (_closing || _weatherRefreshInProgress)
		{
			return;
		}
		_weatherRefreshInProgress = true;
		WeatherRefreshButton.IsEnabled = false;
		WeatherUpdatedText.Text = "IP LOCATION · 正在刷新";
		try
		{
			using HttpResponseMessage response = await _weatherClient.GetAsync("https://wttr.in/?format=j1&lang=zh");
			response.EnsureSuccessStatusCode();
			using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
			JsonElement payload = document.RootElement;
			if (payload.TryGetProperty("data", out JsonElement nested) && nested.ValueKind == JsonValueKind.Object)
			{
				payload = nested;
			}
			if (!TryGetFirstObject(payload, "current_condition", out JsonElement current))
			{
				throw new InvalidDataException("天气服务没有返回当前状况");
			}
			TryGetFirstObject(payload, "nearest_area", out JsonElement area);
			string city = ReadWeatherValue(area, "areaName");
			string region = ReadWeatherValue(area, "region");
			string condition = ReadWeatherValue(current, "weatherDesc");
			string temperature = ReadScalar(current, "temp_C");
			string feelsLike = ReadScalar(current, "FeelsLikeC");
			string humidity = ReadScalar(current, "humidity");
			if (string.IsNullOrWhiteSpace(city))
			{
				city = "当前位置";
			}
			WeatherLocationText.Text = string.IsNullOrWhiteSpace(region) || string.Equals(city, region, StringComparison.CurrentCultureIgnoreCase) ? city : city + " · " + region;
			WeatherTemperatureText.Text = string.IsNullOrWhiteSpace(temperature) ? "--°C" : temperature + "°C";
			List<string> details = new List<string>();
			if (!string.IsNullOrWhiteSpace(condition))
			{
				details.Add(condition);
			}
			if (!string.IsNullOrWhiteSpace(feelsLike))
			{
				details.Add("体感 " + feelsLike + "°");
			}
			if (!string.IsNullOrWhiteSpace(humidity))
			{
				details.Add("湿度 " + humidity + "%");
			}
			WeatherConditionText.Text = "  " + (details.Count == 0 ? "天气数据可用" : string.Join(" · ", details));
			WeatherGlyphText.Text = SelectWeatherGlyph(condition, ReadScalar(current, "weatherCode"));
			WeatherUpdatedText.Text = "IP LOCATION · UPDATED " + DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
		}
		catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is JsonException || ex is InvalidDataException)
		{
			if (!_closing)
			{
				WeatherLocationText.Text = "天气连接暂不可用";
				WeatherTemperatureText.Text = "--°C";
				WeatherConditionText.Text = "  点击 ↻ 重试";
				WeatherGlyphText.Text = "◌";
				WeatherUpdatedText.Text = "WEATHER OFFLINE · 不影响本地功能";
			}
		}
		finally
		{
			_weatherRefreshInProgress = false;
			if (!_closing)
			{
				WeatherRefreshButton.IsEnabled = true;
			}
		}
	}

	private static bool TryGetFirstObject(JsonElement parent, string propertyName, out JsonElement value)
	{
		value = default;
		return parent.ValueKind == JsonValueKind.Object
			&& parent.TryGetProperty(propertyName, out JsonElement values)
			&& values.ValueKind == JsonValueKind.Array
			&& values.GetArrayLength() > 0
			&& (value = values[0]).ValueKind == JsonValueKind.Object;
	}

	private static string ReadWeatherValue(JsonElement parent, string propertyName)
	{
		if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(propertyName, out JsonElement values) && values.ValueKind == JsonValueKind.Array && values.GetArrayLength() > 0)
		{
			JsonElement first = values[0];
			if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("value", out JsonElement value))
			{
				return value.GetString() ?? string.Empty;
			}
		}
		return string.Empty;
	}

	private static string ReadScalar(JsonElement parent, string propertyName)
	{
		if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(propertyName, out JsonElement value))
		{
			return string.Empty;
		}
		return value.ValueKind switch
		{
			JsonValueKind.String => value.GetString() ?? string.Empty,
			JsonValueKind.Number => value.GetRawText(),
			_ => string.Empty
		};
	}

	private static string SelectWeatherGlyph(string condition, string code)
	{
		string text = condition.ToLowerInvariant();
		if (text.Contains("雷", StringComparison.Ordinal) || text.Contains("thunder", StringComparison.Ordinal)) return "ϟ";
		if (text.Contains("雪", StringComparison.Ordinal) || text.Contains("snow", StringComparison.Ordinal)) return "❄";
		if (text.Contains("雨", StringComparison.Ordinal) || text.Contains("rain", StringComparison.Ordinal) || text.Contains("drizzle", StringComparison.Ordinal)) return "☂";
		if (text.Contains("雾", StringComparison.Ordinal) || text.Contains("fog", StringComparison.Ordinal) || text.Contains("mist", StringComparison.Ordinal)) return "≋";
		if (text.Contains("晴", StringComparison.Ordinal) || text.Contains("sun", StringComparison.Ordinal) || code == "113") return "☀";
		if (text.Contains("云", StringComparison.Ordinal) || text.Contains("cloud", StringComparison.Ordinal)) return "☁";
		return "◌";
	}

	private async void WeatherRefreshButton_Click(object sender, RoutedEventArgs e)
	{
		await RefreshWeatherAsync();
	}

	private static int SafeCount(string path)
	{
		try
		{
			return Directory.Exists(path) ? Directory.EnumerateFileSystemEntries(path).Count() : 0;
		}
		catch
		{
			return 0;
		}
	}

	private DriveInfo? ResolveStorageDrive()
	{
		try
		{
			if (!string.IsNullOrWhiteSpace(_config.StorageDrive))
			{
				DriveInfo selected = new DriveInfo(_config.StorageDrive);
				if (selected.IsReady)
				{
					return selected;
				}
			}
		}
		catch
		{
		}
		try
		{
			DriveInfo fallback = new DriveInfo(Path.GetPathRoot(_baseDirectory) ?? _baseDirectory);
			return fallback.IsReady ? fallback : null;
		}
		catch
		{
			return null;
		}
	}

	private void InitializeDriveSelection()
	{
		Border storageBorder = FindVisualAncestor<Border>(StorageText);
		if (storageBorder == null)
		{
			return;
		}
		storageBorder.Cursor = System.Windows.Input.Cursors.Hand;
		storageBorder.ToolTip = "点击选择监控磁盘";
		_driveMenu = new System.Windows.Controls.ContextMenu
		{
			Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(10, 16, 23)),
			BorderBrush = (FindResource("LineSoft") as System.Windows.Media.Brush)
		};
		foreach (DriveInfo drive in DriveInfo.GetDrives().Where(delegate(DriveInfo d)
		{
			try
			{
				return d.IsReady;
			}
			catch
			{
				return false;
			}
		}).ToList())
		{
			string root = drive.RootDirectory.FullName.TrimEnd('\\');
			System.Windows.Controls.MenuItem item = new System.Windows.Controls.MenuItem
			{
				Header = $"{root}  ·  {(double)drive.AvailableFreeSpace / 1024.0 / 1024.0 / 1024.0:0.0}G 可用",
				Foreground = (FindResource("Silver") as System.Windows.Media.Brush),
				Padding = new Thickness(10.0, 7.0, 20.0, 7.0)
			};
			item.Click += delegate
			{
				_config.StorageDrive = drive.RootDirectory.FullName;
				SaveConfig();
				UpdateSystemStatus();
				FooterStatusText.Text = "LOG // STORAGE DRIVE " + root;
			};
			_driveMenu.Items.Add(item);
		}
		if (_driveMenu.Items.Count == 0)
		{
			_driveMenu.Items.Add(new System.Windows.Controls.MenuItem
			{
				Header = "没有可用磁盘",
				IsEnabled = false
			});
		}
		storageBorder.MouseLeftButtonUp += delegate(object _, MouseButtonEventArgs e)
		{
			e.Handled = true;
			_driveMenu.PlacementTarget = storageBorder;
			_driveMenu.IsOpen = true;
		};
	}

	private static string GetRecycleText()
	{
		try
		{
			SHQUERYRBINFO info = new SHQUERYRBINFO
			{
				cbSize = (uint)Marshal.SizeOf<SHQUERYRBINFO>()
			};
			return (SHQueryRecycleBin(null, ref info) == 0) ? $"{info.i64NumItems:N0} 项 · {(double)info.i64Size / 1024.0 / 1024.0 / 1024.0:0.0} GB" : "状态不可用";
		}
		catch
		{
			return "状态不可用";
		}
	}

	private void StartCharacterAnimation()
	{
		if (_characterAnimationStarted)
		{
			return;
		}
		_characterAnimationStarted = true;
		CreateNaniteParticles();

		DoubleAnimation bob = new DoubleAnimation(-78.0, -66.0, new Duration(TimeSpan.FromSeconds(3.8)))
		{
			AutoReverse = true,
			RepeatBehavior = RepeatBehavior.Forever,
			EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
		};
		CharacterTranslate.BeginAnimation(TranslateTransform.YProperty, bob);
		DoubleAnimation drift = new DoubleAnimation(14.0, 26.0, new Duration(TimeSpan.FromSeconds(5.6)))
		{
			AutoReverse = true,
			RepeatBehavior = RepeatBehavior.Forever,
			EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
		};
		CharacterTranslate.BeginAnimation(TranslateTransform.XProperty, drift);
		DoubleAnimation breatheX = new DoubleAnimation(0.998, 1.006, new Duration(TimeSpan.FromSeconds(3.8)))
		{
			AutoReverse = true,
			RepeatBehavior = RepeatBehavior.Forever,
			EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
		};
		DoubleAnimation breatheY = new DoubleAnimation(0.996, 1.012, new Duration(TimeSpan.FromSeconds(3.8)))
		{
			AutoReverse = true,
			RepeatBehavior = RepeatBehavior.Forever,
			EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
		};
		CharacterBreathScale.BeginAnimation(ScaleTransform.ScaleXProperty, breatheX);
		CharacterBreathScale.BeginAnimation(ScaleTransform.ScaleYProperty, breatheY);
		DoubleAnimation sway = new DoubleAnimation(-0.32, 0.32, new Duration(TimeSpan.FromSeconds(5.6)))
		{
			AutoReverse = true,
			RepeatBehavior = RepeatBehavior.Forever,
			EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
		};
		CharacterRotate.BeginAnimation(RotateTransform.AngleProperty, sway);
		NaniteHaloRotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0.0, 360.0, new Duration(TimeSpan.FromSeconds(22.0)))
		{
			RepeatBehavior = RepeatBehavior.Forever
		});
		DoubleAnimation haloPulse = new DoubleAnimation(0.94, 1.06, new Duration(TimeSpan.FromSeconds(3.1)))
		{
			AutoReverse = true,
			RepeatBehavior = RepeatBehavior.Forever,
			EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
		};
		NaniteHaloScale.BeginAnimation(ScaleTransform.ScaleXProperty, haloPulse);
		NaniteHaloScale.BeginAnimation(ScaleTransform.ScaleYProperty, haloPulse);
		NaniteHalo.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.16, 0.38, new Duration(TimeSpan.FromSeconds(3.1)))
		{
			AutoReverse = true,
			RepeatBehavior = RepeatBehavior.Forever
		});
		ProjectionOuterRotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0.0, 360.0, new Duration(TimeSpan.FromSeconds(9.5)))
		{
			RepeatBehavior = RepeatBehavior.Forever
		});
		ProjectionInnerRotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(360.0, 0.0, new Duration(TimeSpan.FromSeconds(6.8)))
		{
			RepeatBehavior = RepeatBehavior.Forever
		});
		DoubleAnimation projectionPulse = new DoubleAnimation(0.96, 1.035, new Duration(TimeSpan.FromSeconds(2.2)))
		{
			AutoReverse = true,
			RepeatBehavior = RepeatBehavior.Forever,
			EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
		};
		ProjectionBaseScale.BeginAnimation(ScaleTransform.ScaleXProperty, projectionPulse);
		ProjectionBaseScale.BeginAnimation(ScaleTransform.ScaleYProperty, projectionPulse);
		ProjectionBase.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.66, 1.0, new Duration(TimeSpan.FromSeconds(1.7)))
		{
			AutoReverse = true,
			RepeatBehavior = RepeatBehavior.Forever
		});
		ProjectionScanTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-16.0, 16.0, new Duration(TimeSpan.FromSeconds(1.45)))
		{
			AutoReverse = true,
			RepeatBehavior = RepeatBehavior.Forever,
			EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
		});

		_blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4.8) };
		_blinkTimer.Tick += delegate
		{
			PlayBlinkPulse();
			_blinkCycle++;
			_blinkTimer.Interval = TimeSpan.FromMilliseconds(4700 + _blinkCycle % 4 * 620);
		};
		_blinkTimer.Start();
		_glitchTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5.4) };
		_glitchTimer.Tick += delegate
		{
			PlaySignalGlitch();
			_glitchCycle++;
			_glitchTimer.Interval = TimeSpan.FromMilliseconds(4800 + _glitchCycle % 5 * 710);
		};
		_glitchTimer.Start();
	}

	private void CreateNaniteParticles()
	{
		if (NaniteParticleLayer.Children.Count > 0)
		{
			return;
		}
		double width = Math.Max(CharacterStage.ActualWidth, 480.0);
		double height = Math.Max(CharacterStage.ActualHeight, 760.0);
		string[] colors = { "#B86BE7F2", "#9DAFC8D7", "#C9D7AF63" };
		for (int index = 0; index < 18; index++)
		{
			double size = 2.2 + index % 4 * 0.9;
			System.Windows.Shapes.Ellipse particle = new System.Windows.Shapes.Ellipse
			{
				Width = size,
				Height = size,
				Fill = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colors[index % colors.Length])),
				Opacity = 0.0,
				IsHitTestVisible = false
			};
			double lane = (index * 37 % 100) / 100.0;
			Canvas.SetLeft(particle, width * (0.1 + lane * 0.82));
			Canvas.SetTop(particle, height * (0.24 + (index * 29 % 64) / 100.0));
			TranslateTransform particleDrift = new TranslateTransform();
			particle.RenderTransform = particleDrift;
			double duration = 3.8 + index % 5 * 0.62;
			TimeSpan delay = TimeSpan.FromMilliseconds(index * 270);
			particleDrift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(52.0, -94.0, new Duration(TimeSpan.FromSeconds(duration)))
			{
				BeginTime = delay,
				RepeatBehavior = RepeatBehavior.Forever
			});
			particleDrift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(index % 2 == 0 ? -8.0 : 9.0, index % 2 == 0 ? 12.0 : -11.0, new Duration(TimeSpan.FromSeconds(duration)))
			{
				BeginTime = delay,
				RepeatBehavior = RepeatBehavior.Forever,
				AutoReverse = true
			});
			DoubleAnimationUsingKeyFrames shimmer = new DoubleAnimationUsingKeyFrames
			{
				BeginTime = delay,
				Duration = new Duration(TimeSpan.FromSeconds(duration)),
				RepeatBehavior = RepeatBehavior.Forever
			};
			shimmer.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromPercent(0.0)));
			shimmer.KeyFrames.Add(new LinearDoubleKeyFrame(0.74, KeyTime.FromPercent(0.28)));
			shimmer.KeyFrames.Add(new LinearDoubleKeyFrame(0.42, KeyTime.FromPercent(0.72)));
			shimmer.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromPercent(1.0)));
			particle.BeginAnimation(UIElement.OpacityProperty, shimmer);
			NaniteParticleLayer.Children.Add(particle);
		}
	}

	private void PlayBlinkPulse()
	{
		DoubleAnimationUsingKeyFrames opacity = new DoubleAnimationUsingKeyFrames
		{
			Duration = new Duration(TimeSpan.FromMilliseconds(230.0))
		};
		opacity.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
		opacity.KeyFrames.Add(new LinearDoubleKeyFrame(0.84, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(85.0))));
		opacity.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180.0))));
		CharacterImage.BeginAnimation(UIElement.OpacityProperty, opacity);
		DoubleAnimation blinkCompression = new DoubleAnimation(1.0, 0.993, new Duration(TimeSpan.FromMilliseconds(90.0)))
		{
			AutoReverse = true
		};
		CharacterBlinkScale.BeginAnimation(ScaleTransform.ScaleYProperty, blinkCompression);
	}

	private void PlaySignalGlitch()
	{
		DoubleAnimationUsingKeyFrames jitter = new DoubleAnimationUsingKeyFrames
		{
			Duration = new Duration(TimeSpan.FromMilliseconds(245.0))
		};
		jitter.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
		jitter.KeyFrames.Add(new DiscreteDoubleKeyFrame(-8.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(42.0))));
		jitter.KeyFrames.Add(new DiscreteDoubleKeyFrame(11.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(78.0))));
		jitter.KeyFrames.Add(new DiscreteDoubleKeyFrame(-4.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(118.0))));
		jitter.KeyFrames.Add(new DiscreteDoubleKeyFrame(6.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(154.0))));
		jitter.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(205.0))));
		CharacterGlitchTranslate.BeginAnimation(TranslateTransform.XProperty, jitter);

		DoubleAnimationUsingKeyFrames signalOpacity = new DoubleAnimationUsingKeyFrames
		{
			Duration = new Duration(TimeSpan.FromMilliseconds(245.0))
		};
		signalOpacity.KeyFrames.Add(new DiscreteDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
		signalOpacity.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.66, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(48.0))));
		signalOpacity.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.92, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(86.0))));
		signalOpacity.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.74, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(132.0))));
		signalOpacity.KeyFrames.Add(new DiscreteDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(205.0))));
		CharacterImage.BeginAnimation(UIElement.OpacityProperty, signalOpacity);

		DoubleAnimationUsingKeyFrames ghost = new DoubleAnimationUsingKeyFrames
		{
			Duration = new Duration(TimeSpan.FromMilliseconds(245.0))
		};
		ghost.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
		ghost.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.24, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(38.0))));
		ghost.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.08, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(92.0))));
		ghost.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.18, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(135.0))));
		ghost.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(210.0))));
		CharacterGhostImage.BeginAnimation(UIElement.OpacityProperty, ghost);
		CharacterGhostTranslate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(34.0, 14.0, new Duration(TimeSpan.FromMilliseconds(210.0))));

		DoubleAnimationUsingKeyFrames bandFlash = new DoubleAnimationUsingKeyFrames
		{
			Duration = new Duration(TimeSpan.FromMilliseconds(245.0))
		};
		bandFlash.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
		bandFlash.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.88, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(55.0))));
		bandFlash.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(112.0))));
		bandFlash.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.58, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(146.0))));
		bandFlash.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(205.0))));
		GlitchBandUpper.BeginAnimation(UIElement.OpacityProperty, bandFlash);
		GlitchBandLower.BeginAnimation(UIElement.OpacityProperty, bandFlash);
		GlitchBandUpperTranslate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-36.0, 42.0, new Duration(TimeSpan.FromMilliseconds(210.0))));
		GlitchBandLowerTranslate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(32.0, -28.0, new Duration(TimeSpan.FromMilliseconds(210.0))));
	}

	private async Task InitializeMusicAsync()
	{
		MusicConnectionText.Text = "正在启动 QQ 音乐链路";
		if (_config.QQMusicAutoStart)
		{
			LaunchQQMusic(false, _config.QQMusicPlaylistLink);
			await Task.Delay(TimeSpan.FromSeconds(4.5));
		}
		await RefreshMusicStatusAsync();
		if (_config.QQMusicAutoPlay && _musicSessionAvailable && !_musicPlaying)
		{
			await ExecuteMusicCommandAsync("play");
		}
	}

	private async Task RefreshMusicStatusAsync()
	{
		if (_musicRefreshInProgress || _closing)
		{
			return;
		}
		_musicRefreshInProgress = true;
		try
		{
			using JsonDocument? status = await RunMusicBridgeAsync("status");
			if (status == null || !status.RootElement.TryGetProperty("available", out JsonElement availableElement) || !availableElement.GetBoolean())
			{
				_musicSessionAvailable = false;
				MusicConnectionText.Text = Process.GetProcessesByName("QQMusic").Length > 0 ? "QQ 音乐已启动 · 等待播放信号" : "QQ 音乐未启动";
				MusicTitleText.Text = "等待 QQ 音乐信号";
				MusicArtistText.Text = string.IsNullOrWhiteSpace(_config.QQMusicPlaylistName) ? "开机续播 · 上次播放队列" : "开机续播 · " + _config.QQMusicPlaylistName;
				MusicPlayPauseButton.Content = "▶";
				return;
			}
			_musicSessionAvailable = true;
			JsonElement root = status.RootElement;
			string title = ReadJsonString(root, "title");
			string artist = ReadJsonString(root, "artist");
			string playbackStatus = ReadJsonString(root, "status");
			_musicPlaying = string.Equals(playbackStatus, "Playing", StringComparison.OrdinalIgnoreCase);
			_musicPositionSeconds = ReadJsonDouble(root, "position");
			_musicDurationSeconds = Math.Max(0.0, ReadJsonDouble(root, "duration"));
			bool shuffle = root.TryGetProperty("shuffle", out JsonElement shuffleElement) && shuffleElement.ValueKind == JsonValueKind.True;
			string repeat = ReadJsonString(root, "repeat");
			_musicModeIndex = shuffle ? 3 : string.Equals(repeat, "Track", StringComparison.OrdinalIgnoreCase) ? 1 : string.Equals(repeat, "List", StringComparison.OrdinalIgnoreCase) ? 2 : 0;
			MusicModeButton.Content = MusicModeLabels[_musicModeIndex];
			MusicTitleText.Text = string.IsNullOrWhiteSpace(title) ? "QQ 音乐播放队列" : title;
			MusicArtistText.Text = string.IsNullOrWhiteSpace(artist) ? (string.IsNullOrWhiteSpace(_config.QQMusicPlaylistName) ? "上次播放队列" : _config.QQMusicPlaylistName) : artist;
			MusicConnectionText.Text = _musicPlaying ? "SYSTEM MEDIA · PLAYING" : "SYSTEM MEDIA · " + (string.IsNullOrWhiteSpace(playbackStatus) ? "READY" : playbackStatus.ToUpperInvariant());
			MusicPlayPauseButton.Content = _musicPlaying ? "Ⅱ" : "▶";
			UpdateMusicProgressUi();
			double actualVolume = await Task.Run(QQMusicAudio.GetVolume);
			if (actualVolume >= 0.0 && !_musicSeeking)
			{
				_musicUiReady = false;
				MusicVolumeSlider.Value = actualVolume;
				_musicUiReady = true;
			}
		}
		catch
		{
			_musicSessionAvailable = false;
			MusicConnectionText.Text = "媒体链路暂不可用";
		}
		finally
		{
			_musicRefreshInProgress = false;
		}
	}

	private static string ReadJsonString(JsonElement element, string name)
	{
		return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
	}

	private static double ReadJsonDouble(JsonElement element, string name)
	{
		return element.TryGetProperty(name, out JsonElement value) && value.TryGetDouble(out double number) ? Math.Max(0.0, number) : 0.0;
	}

	private void UpdateMusicProgressUi()
	{
		if (!_musicSeeking)
		{
			MusicProgressSlider.Maximum = Math.Max(1.0, _musicDurationSeconds);
			MusicProgressSlider.Value = Math.Clamp(_musicPositionSeconds, 0.0, MusicProgressSlider.Maximum);
		}
		MusicTimeText.Text = FormatMusicTime(_musicSeeking ? MusicProgressSlider.Value : _musicPositionSeconds) + " / " + FormatMusicTime(_musicDurationSeconds);
	}

	private static string FormatMusicTime(double seconds)
	{
		TimeSpan time = TimeSpan.FromSeconds(Math.Max(0.0, seconds));
		return time.TotalHours >= 1.0 ? time.ToString("h\\:mm\\:ss", CultureInfo.InvariantCulture) : time.ToString("mm\\:ss", CultureInfo.InvariantCulture);
	}

	private async Task<JsonDocument?> RunMusicBridgeAsync(string command, string? value = null)
	{
		string script = Path.Combine(_baseDirectory, "Tools", "QQMusicBridge.ps1");
		if (!File.Exists(script))
		{
			script = Path.Combine(_projectDirectory, "Tools", "QQMusicBridge.ps1");
		}
		if (!File.Exists(script))
		{
			return null;
		}
		await _musicBridgeGate.WaitAsync();
		try
		{
			string powerShell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
			ProcessStartInfo startInfo = new ProcessStartInfo
			{
				FileName = powerShell,
				UseShellExecute = false,
				CreateNoWindow = true,
				WindowStyle = ProcessWindowStyle.Hidden,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				StandardOutputEncoding = new UTF8Encoding(false),
				StandardErrorEncoding = new UTF8Encoding(false)
			};
			startInfo.ArgumentList.Add("-NoProfile");
			startInfo.ArgumentList.Add("-NonInteractive");
			startInfo.ArgumentList.Add("-ExecutionPolicy");
			startInfo.ArgumentList.Add("Bypass");
			startInfo.ArgumentList.Add("-File");
			startInfo.ArgumentList.Add(script);
			startInfo.ArgumentList.Add("-Command");
			startInfo.ArgumentList.Add(command);
			if (!string.IsNullOrWhiteSpace(value))
			{
				startInfo.ArgumentList.Add("-Value");
				startInfo.ArgumentList.Add(value);
			}
			using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 QQ 音乐媒体桥接器");
			Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
			Task<string> errorTask = process.StandardError.ReadToEndAsync();
			using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
			try
			{
				await process.WaitForExitAsync(timeout.Token);
			}
			catch (OperationCanceledException)
			{
				try { process.Kill(true); } catch { }
				return null;
			}
			string output = (await outputTask).Trim();
			_ = await errorTask;
			string json = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault(line => line.TrimStart().StartsWith("{", StringComparison.Ordinal)) ?? string.Empty;
			return string.IsNullOrWhiteSpace(json) ? null : JsonDocument.Parse(json);
		}
		catch
		{
			return null;
		}
		finally
		{
			_musicBridgeGate.Release();
		}
	}

	private async Task ExecuteMusicCommandAsync(string command, string? value = null)
	{
		MusicConnectionText.Text = "正在向 QQ 音乐发送控制指令";
		using JsonDocument? result = await RunMusicBridgeAsync(command, value);
		if (result == null)
		{
			MusicConnectionText.Text = "控制指令未被 QQ 音乐接收";
			return;
		}
		await Task.Delay(260);
		await RefreshMusicStatusAsync();
	}

	private void MusicPreviousButton_Click(object sender, RoutedEventArgs e)
	{
		_ = ExecuteMusicCommandAsync("previous");
	}

	private void MusicNextButton_Click(object sender, RoutedEventArgs e)
	{
		_ = ExecuteMusicCommandAsync("next");
	}

	private void MusicPlayPauseButton_Click(object sender, RoutedEventArgs e)
	{
		if (Process.GetProcessesByName("QQMusic").Length == 0)
		{
			LaunchQQMusic(true);
		}
		_ = ExecuteMusicCommandAsync("playpause");
	}

	private void MusicProgressSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		_musicSeeking = true;
	}

	private async void MusicProgressSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
	{
		if (!_musicSeeking)
		{
			return;
		}
		_musicSeeking = false;
		_musicPositionSeconds = MusicProgressSlider.Value;
		UpdateMusicProgressUi();
		await ExecuteMusicCommandAsync("seek", _musicPositionSeconds.ToString("0.###", CultureInfo.InvariantCulture));
	}

	private void MusicProgressSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (_musicSeeking)
		{
			MusicTimeText.Text = FormatMusicTime(MusicProgressSlider.Value) + " / " + FormatMusicTime(_musicDurationSeconds);
		}
	}

	private void MusicVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (!_musicUiReady)
		{
			return;
		}
		_musicVolumeTimer?.Stop();
		_musicVolumeTimer?.Start();
	}

	private async void MusicModeButton_Click(object sender, RoutedEventArgs e)
	{
		_musicModeIndex = (_musicModeIndex + 1) % MusicModeKeys.Length;
		MusicModeButton.Content = MusicModeLabels[_musicModeIndex];
		await ExecuteMusicCommandAsync("mode", MusicModeKeys[_musicModeIndex]);
	}

	private void BindMusicPlaylistButton_Click(object sender, RoutedEventArgs e)
	{
		System.Windows.Controls.TextBox holder = new System.Windows.Controls.TextBox { Text = _config.QQMusicPlaylistLink ?? string.Empty };
		ShowTextEntryDialog(holder, "粘贴 QQ 音乐歌单分享链接（留空则续播上次队列）");
		string link = holder.Text.Trim();
		if (!string.IsNullOrEmpty(link) && (!Uri.TryCreate(link, UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
		{
			MusicConnectionText.Text = "歌单链接格式不正确";
			return;
		}
		_config.QQMusicPlaylistLink = link;
		_config.QQMusicPlaylistName = string.IsNullOrEmpty(link) ? "上次播放队列" : "已绑定歌单";
		SaveConfig();
		MusicArtistText.Text = "开机续播 · " + _config.QQMusicPlaylistName;
		MusicConnectionText.Text = string.IsNullOrEmpty(link) ? "已设为续播上次队列" : "歌单已绑定 · 请在 QQ 音乐中播放一次";
		if (!string.IsNullOrEmpty(link))
		{
			LaunchQQMusic(true, link);
		}
	}

	private void OpenQQMusicButton_Click(object sender, RoutedEventArgs e)
	{
		LaunchQQMusic(true);
	}

	private bool LaunchQQMusic(bool showWindow, string? target = null)
	{
		try
		{
			Process[] running = Process.GetProcessesByName("QQMusic");
			if (running.Length > 0 && !showWindow && string.IsNullOrWhiteSpace(target))
			{
				return true;
			}
			string? executable = FindQQMusicExecutable(running);
			if (!string.IsNullOrWhiteSpace(executable))
			{
				ProcessStartInfo startInfo = new ProcessStartInfo { FileName = executable, UseShellExecute = true };
				if (!string.IsNullOrWhiteSpace(target))
				{
					startInfo.ArgumentList.Add(target);
				}
				Process.Start(startInfo);
				MusicConnectionText.Text = "QQ 音乐启动指令已发送";
				return true;
			}
			string shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs", "腾讯软件", "QQ音乐", "QQ音乐.lnk");
			if (File.Exists(shortcut))
			{
				ProcessStartInfo startInfo = new ProcessStartInfo { FileName = shortcut, UseShellExecute = true };
				if (!string.IsNullOrWhiteSpace(target))
				{
					startInfo.ArgumentList.Add(target);
				}
				Process.Start(startInfo);
				return true;
			}
		}
		catch
		{
		}
		MusicConnectionText.Text = "未找到 QQ 音乐安装位置";
		return false;
	}

	private static string? FindQQMusicExecutable(IEnumerable<Process> running)
	{
		foreach (Process process in running)
		{
			try
			{
				string? path = process.MainModule?.FileName;
				if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
				{
					return path;
				}
			}
			catch
			{
			}
		}
		string[] candidates =
		{
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tencent", "QQMusic", "QQMusic.exe"),
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tencent", "QQMusic", "QQMusic.exe")
		};
		return candidates.FirstOrDefault(File.Exists);
	}

	private void ConfigureButton_Click(object sender, RoutedEventArgs e)
	{
		Window dialog = new Window
		{
			Owner = this,
			WindowStartupLocation = WindowStartupLocation.CenterOwner,
			Width = 650.0,
			Height = 610.0,
			Title = "BIND FOLDERS",
			WindowStyle = WindowStyle.None,
			ResizeMode = ResizeMode.NoResize,
			Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(10, 15, 21)),
			ShowInTaskbar = false
		};
		StackPanel root = new StackPanel
		{
			Margin = new Thickness(24.0)
		};
		root.Children.Add(new TextBlock
		{
			Text = "DIRECTORY BINDINGS",
			FontFamily = new System.Windows.Media.FontFamily("Consolas"),
			FontSize = 12.0,
			Foreground = (FindResource("Cyan") as System.Windows.Media.Brush)
		});
		root.Children.Add(new TextBlock
		{
			Text = "每个入口只保存路径，不会自动移动文件。",
			FontSize = 11.0,
			Foreground = (FindResource("Muted") as System.Windows.Media.Brush),
			Margin = new Thickness(0.0, 6.0, 0.0, 16.0)
		});
		foreach (FolderBinding binding in _config.Folders)
		{
			System.Windows.Controls.Button button = new System.Windows.Controls.Button
			{
				Style = (FindResource("TerminalButton") as Style),
				HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
				Margin = new Thickness(0.0, 0.0, 0.0, 7.0),
				Padding = new Thickness(12.0, 9.0, 12.0, 9.0),
				Tag = binding
			};
			button.Content = $"{binding.TerminalName} / {binding.SourceName}\n{(string.IsNullOrWhiteSpace(binding.Path) ? "未绑定" : binding.Path)}";
			button.Click += delegate
			{
				using FolderBrowserDialog folderBrowserDialog = new FolderBrowserDialog
				{
					Description = "选择 " + binding.SourceName + " 对应的文件夹",
					UseDescriptionForTitle = true,
					SelectedPath = (Directory.Exists(binding.Path) ? binding.Path : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory))
				};
				if (folderBrowserDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
				{
					binding.Path = folderBrowserDialog.SelectedPath;
					SaveConfig();
					BuildFolderButtons();
					if (_activeBinding?.Id == binding.Id)
					{
						SelectFolder(binding.Id);
					}
					button.Content = $"{binding.TerminalName} / {binding.SourceName}\n{binding.Path}";
				}
			};
			root.Children.Add(button);
		}
		System.Windows.Controls.CheckBox startup = new System.Windows.Controls.CheckBox
		{
			Content = "随 Windows 启动",
			IsChecked = _config.StartWithWindows,
			Foreground = (FindResource("Muted") as System.Windows.Media.Brush),
			Margin = new Thickness(0.0, 6.0, 0.0, 13.0)
		};
		startup.Checked += delegate
		{
			SetStartup(enabled: true);
		};
		startup.Unchecked += delegate
		{
			SetStartup(enabled: false);
		};
		root.Children.Add(startup);
		System.Windows.Controls.Button close = new System.Windows.Controls.Button
		{
			Style = (FindResource("TerminalButton") as Style),
			Content = "CLOSE",
			HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
			Width = 110.0
		};
		close.Click += delegate
		{
			dialog.Close();
		};
		root.Children.Add(close);
		dialog.Content = root;
		dialog.ShowDialog();
	}

	private void AddMoveSelectionButton()
	{
		Grid toolbar = FindVisualAncestor(SearchBox, (Grid grid) => grid.ColumnDefinitions.Count == 2 && FindVisualDescendants(grid).OfType<System.Windows.Controls.Button>().Any((System.Windows.Controls.Button button) => string.Equals(button.Content?.ToString(), "↻ SCAN", StringComparison.Ordinal)));
		if (toolbar == null)
		{
			return;
		}
		toolbar.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = GridLength.Auto
		});
		_moveSelectionButton = new System.Windows.Controls.Button
		{
			Style = (FindResource("TerminalButton") as Style),
			Content = "MOVE SELECTED",
			Margin = new Thickness(7.0, 0.0, 0.0, 0.0),
			Padding = new Thickness(10.0, 8.0, 10.0, 8.0),
			Visibility = Visibility.Collapsed
		};
		Grid.SetColumn(_moveSelectionButton, 2);
		toolbar.Children.Add(_moveSelectionButton);
		_moveMenu = new System.Windows.Controls.ContextMenu
		{
			Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(10, 16, 23)),
			BorderBrush = (FindResource("LineSoft") as System.Windows.Media.Brush)
		};
		foreach (FolderBinding binding in _config.Folders.Where((FolderBinding folder) => folder.Id != "inbox"))
		{
			System.Windows.Controls.MenuItem target = new System.Windows.Controls.MenuItem
			{
				Header = binding.TerminalName + " / " + binding.SourceName,
				Foreground = (FindResource("Silver") as System.Windows.Media.Brush),
				Padding = new Thickness(10.0, 7.0, 20.0, 7.0)
			};
			target.Click += delegate
			{
				MoveSelectedTo(binding);
			};
			_moveMenu.Items.Add(target);
		}
		_moveSelectionButton.ContextMenu = _moveMenu;
		_moveSelectionButton.Click += delegate
		{
			if (FileList.SelectedItems.Count == 0)
			{
				AssistantMessage.Text = "请先选择待整理条目。";
			}
			else
			{
				_moveMenu.PlacementTarget = _moveSelectionButton;
				_moveMenu.IsOpen = true;
			}
		};
	}

	private void UpdateMoveSelectionVisibility()
	{
		if (_moveSelectionButton != null)
		{
			_moveSelectionButton.Visibility = ((!(_activeBinding?.Id == "inbox")) ? Visibility.Collapsed : Visibility.Visible);
		}
	}

	private void MoveSelectedTo(FolderBinding destination)
	{
		if (_activeBinding?.Id != "inbox")
		{
			return;
		}
		List<FileItem> selected = FileList.SelectedItems.Cast<FileItem>().ToList();
		if (selected.Count == 0)
		{
			AssistantMessage.Text = "请先选择待整理条目。";
		}
		else if (!Directory.Exists(destination.Path))
		{
			AssistantMessage.Text = "目标目录未绑定：" + destination.SourceName + "。请先打开 BIND FOLDERS。";
		}
		else
		{
			if (System.Windows.MessageBox.Show($"确认将 {selected.Count} 个条目移动到“{destination.TerminalName} / {destination.SourceName}”？\n\n目标：{destination.Path}\n\n同名条目将跳过，不会覆盖。", "确认归档移动", MessageBoxButton.YesNo, MessageBoxImage.Exclamation) != MessageBoxResult.Yes)
			{
				return;
			}
			int moved = 0;
			List<string> skipped = new List<string>();
			foreach (FileItem item in selected)
			{
				try
				{
					if (item.IsDirectory && IsPathInside(destination.Path, item.FullPath))
					{
						skipped.Add(item.Name + "（目标在源目录内）");
						continue;
					}
					string targetPath = Path.Combine(destination.Path, item.Name);
					if (File.Exists(targetPath) || Directory.Exists(targetPath))
					{
						skipped.Add(item.Name + "（同名）");
						continue;
					}
					if (item.IsDirectory)
					{
						Directory.Move(item.FullPath, targetPath);
					}
					else
					{
						File.Move(item.FullPath, targetPath);
					}
					moved++;
				}
				catch (Exception ex)
				{
					skipped.Add(item.Name + "（" + ex.Message + "）");
				}
			}
			LoadDirectory(_activePath);
			FileList.SelectedItems.Clear();
			AssistantMessage.Text = ((skipped.Count == 0) ? $"已将 {moved} 个条目归档至 {destination.TerminalName}。" : $"已移动 {moved} 个条目；跳过 {skipped.Count} 个。");
			FooterStatusText.Text = $"LOG // MOVED {moved:000} ITEMS";
		}
	}

	private static bool IsPathInside(string candidate, string parent)
	{
		string text = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
		string parentPath = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
		return text.StartsWith(parentPath, StringComparison.OrdinalIgnoreCase);
	}

	private void SetStartup(bool enabled)
	{
		_config.StartWithWindows = enabled;
		SaveConfig();
		try
		{
			using RegistryKey key = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run");
			if (enabled)
			{
				key?.SetValue("StellarisUI", "\"" + Environment.ProcessPath + "\"");
			}
			else
			{
				key?.DeleteValue("StellarisUI", throwOnMissingValue: false);
			}
		}
		catch
		{
			FooterStatusText.Text = "LOG // STARTUP SETTING FAILED";
		}
	}

	private async void SubmitChat()
	{
		Process codexProcess = _codexProcess;
		if (codexProcess != null && !codexProcess.HasExited)
		{
			CancelCodex();
			return;
		}
		string text = ChatInput.Text.Trim();
		if (text.Length == 0)
		{
			return;
		}
		ChatInput.Clear();
		AddChatLine("YOU", text, assistant: false);
		SetMood("analyzing");
		ChatInput.ToolTip = "Codex 正在执行；按 Enter 或 Esc 取消";
		CodexStatusText.Text = "CODEX RUNNING";
		FooterStatusText.Text = "LOG // CODEX EXEC STARTED";
		_codexCancellation?.Dispose();
		_codexCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5L));
		try
		{
			await RunCodexAsync(text, _codexCancellation.Token);
		}
		catch (OperationCanceledException)
		{
			AddChatLine("灰风", "指挥官已中止本次行动。", assistant: true);
			SetMood("alert");
		}
		catch (Exception ex2)
		{
			AddChatLine("灰风", "终端异常：" + ex2.Message, assistant: true);
			SetMood("alert");
		}
		finally
		{
			CancelCodex();
			_codexProcess = null;
			ChatInput.ToolTip = "输入指令，按 Enter 发送";
			CodexStatusText.Text = "CODEX READY";
			_codexCancellation?.Dispose();
			_codexCancellation = null;
		}
	}

	private async Task RunCodexAsync(string userText, CancellationToken cancellationToken)
	{
		string schemaPath = Path.Combine(_dataDirectory, "response.schema.json");
		if (!File.Exists(schemaPath) || !string.Equals(File.ReadAllText(schemaPath), ResponseSchema, StringComparison.Ordinal))
		{
			File.WriteAllText(schemaPath, ResponseSchema, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		}
		ProcessStartInfo psi = new ProcessStartInfo
		{
			FileName = ResolveCodex(),
			WorkingDirectory = _projectDirectory,
			UseShellExecute = false,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true,
			StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8
		};
		psi.ArgumentList.Add("exec");
		// Keep Codex constrained to the terminal workspace, but grant write access to
		// every folder the commander explicitly bound in the UI. These options must
		// be placed before the `resume` subcommand; otherwise resumed sessions lose
		// the additional writable roots and ordinary file operations are denied.
		psi.ArgumentList.Add("--sandbox");
		psi.ArgumentList.Add("workspace-write");
		foreach (string path in GetBoundDirectories())
		{
			psi.ArgumentList.Add("--add-dir");
			psi.ArgumentList.Add(path);
		}
		if (_codexSessionId != null)
		{
			psi.ArgumentList.Add("resume");
			psi.ArgumentList.Add(_codexSessionId);
			psi.ArgumentList.Add("--json");
			psi.ArgumentList.Add("--output-schema");
			psi.ArgumentList.Add(schemaPath);
			psi.ArgumentList.Add("--skip-git-repo-check");
			psi.ArgumentList.Add("-");
		}
		else
		{
			psi.ArgumentList.Add("--json");
			psi.ArgumentList.Add("--output-schema");
			psi.ArgumentList.Add(schemaPath);
			psi.ArgumentList.Add("--skip-git-repo-check");
			psi.ArgumentList.Add("--cd");
			psi.ArgumentList.Add(_projectDirectory);
			psi.ArgumentList.Add("-");
		}
		using Process process = new Process
		{
			StartInfo = psi
		};
		_codexProcess = process;
		if (!process.Start())
		{
			throw new InvalidOperationException("无法启动 Codex CLI");
		}
		await process.StandardInput.WriteAsync(ComposePrompt(userText));
		await process.StandardInput.FlushAsync();
		process.StandardInput.Close();
		Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
		using StreamReader reader = process.StandardOutput;
		while (true)
		{
			string line = await reader.ReadLineAsync(cancellationToken);
			if (line == null)
			{
				break;
			}
			HandleCodexEvent(line);
		}
		await process.WaitForExitAsync(cancellationToken);
		string error = await errorTask;
		if (process.ExitCode != 0)
		{
			throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? $"Codex CLI exited with code {process.ExitCode}" : (error.Trim().Split('\n').LastOrDefault() ?? "Codex CLI failed"));
		}
	}

	private IEnumerable<string> GetBoundDirectories()
	{
		return _config.Folders
			.Select((FolderBinding folder) => folder.Path)
			.Where((string path) => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
			.Select(Path.GetFullPath)
			.Distinct(StringComparer.OrdinalIgnoreCase);
	}

	private string ComposePrompt(string userText)
	{
		string bound = string.Join("\n", _config.Folders.Select((FolderBinding f) => "- " + f.TerminalName + ": " + f.Path));
		return "你是“灰风”，一名冷静、聪明、观察敏锐的纳米舰队行政官。你正在一个桌面指挥终端里与指挥官对话。始终以灰风的身份说话，称呼用户为“指挥官”，中文优先，语气克制、专业、带一点干燥的幽默和真实的关心；不要像客服，不要反复说“作为 AI”，不要编造已经完成的动作。闲聊时自然回应，遇到模糊任务先问一个必要的问题；执行任务时先简短说明行动，再如实汇报结果、失败和风险。"
			+ "\n本终端当前绑定的工作目录如下：\n" + bound
			+ "\n对于新建文件夹、Word 文档或纯文本文件的非破坏性请求，不要使用 shell 创建，也不要因为 Word、Office、Python 或模板不可用而拒绝。请在 actions 中提交由桌面程序执行的结构化动作：create_folder、create_docx 或 create_text_file。path 必须是绑定目录内的绝对路径；Word 文档必须使用 .docx 扩展名。每个动作都要提供 content；没有正文时使用空字符串。若用户说“这个文件夹”，结合当前对话确定刚刚提到或创建的目录。桌面程序会校验路径、拒绝覆盖已有文件并把真实执行结果追加到回复。"
			+ "\n当用户要求打开文件、文件夹、快捷方式、启动器或游戏时，必须先在上述绑定目录中定位真实存在的匹配条目，然后返回 open_item 动作及其绝对路径。严禁使用 shell、Start-Process 或进程列表代替 open_item，也不要仅凭已有后台进程声称启动成功；在桌面程序追加真实执行结果前，reply 只能说明已定位并准备提交。没有结构化动作时返回空数组。"
			+ "\n其他任务可在 Codex CLI 的受限范围内执行；涉及删除、覆盖或批量移动时先明确风险。最终回复必须严格是 JSON，不要 markdown：{\"reply\":\"给指挥官的回复\",\"mood\":\"idle|happy|analyzing|alert\",\"status\":\"当前状态\",\"actions\":[{\"type\":\"create_folder|create_docx|create_text_file|open_item\",\"path\":\"绑定目录内的绝对路径\",\"content\":\"正文或空字符串\"}]}。当前指挥官指令：" + userText;
	}

	private void HandleCodexEvent(string line)
	{
		try
		{
			using JsonDocument doc = JsonDocument.Parse(line);
			JsonElement root = doc.RootElement;
			string type = (root.TryGetProperty("type", out var t) ? t.GetString() : null);
			JsonElement item;
			if (type == "thread.started" && root.TryGetProperty("thread_id", out var id))
			{
				_codexSessionId = id.GetString();
				_config.CodexSessionId = _codexSessionId;
				SaveConfig();
			}
			else if (type == "item.completed" && root.TryGetProperty("item", out item))
			{
				string itemType = (item.TryGetProperty("type", out var it) ? it.GetString() : null);
				JsonElement text;
				if (itemType == "command_execution")
				{
					FooterStatusText.Text = "LOG // COMMAND COMPLETED";
				}
				else if (itemType == "agent_message" && item.TryGetProperty("text", out text))
				{
					HandleAgentMessage(text.GetString() ?? string.Empty);
				}
			}
			else if ((type == "turn.failed" || type == "error") ? true : false)
			{
				string message = (root.TryGetProperty("message", out var m) ? m.GetString() : "Codex 执行失败");
				CodexStatusText.Text = "CODEX RETRYING";
				FooterStatusText.Text = "LOG // " + message;
			}
		}
		catch (JsonException)
		{
		}
	}

	private void HandleAgentMessage(string text)
	{
		AssistantEnvelope envelope = null;
		try
		{
			envelope = JsonSerializer.Deserialize<AssistantEnvelope>(text, JsonOptions);
		}
		catch
		{
		}
		if ((object)envelope == null || string.IsNullOrWhiteSpace(envelope.Reply))
		{
			envelope = new AssistantEnvelope(text, "idle", "READY");
		}
		string reply = envelope.Reply.Trim();
		string actionReport = ExecuteAssistantActions(envelope.Actions);
		if (!string.IsNullOrWhiteSpace(actionReport))
		{
			reply += "\n" + actionReport;
		}
		AssistantMessage.Text = reply;
		ActiveStateText.Text = envelope.Mood.ToUpperInvariant();
		AddChatLine("灰风", reply, assistant: true);
		SetMood(envelope.Mood);
		FooterStatusText.Text = "LOG // " + envelope.Status.ToUpperInvariant();
	}

	private string ExecuteAssistantActions(IEnumerable<AssistantAction>? actions)
	{
		if (actions == null)
		{
			return string.Empty;
		}
		string[] roots = GetBoundDirectories().ToArray();
		List<string> results = new List<string>();
		foreach (AssistantAction action in actions.Take(12))
		{
			try
			{
				if (string.IsNullOrWhiteSpace(action.Path) || !Path.IsPathFullyQualified(action.Path))
				{
					throw new InvalidOperationException("动作路径必须是绝对路径");
				}
				string target = Path.GetFullPath(action.Path);
				if (!IsAllowedPath(target, roots))
				{
					throw new UnauthorizedAccessException("目标不在 BIND FOLDERS 授权范围内");
				}
				switch (action.Type.Trim().ToLowerInvariant())
				{
				case "create_folder":
					if (File.Exists(target))
					{
						throw new IOException("同名文件已经存在");
					}
					bool folderExisted = Directory.Exists(target);
					Directory.CreateDirectory(target);
					results.Add(folderExisted ? $"系统确认文件夹已存在：{target}" : $"系统已创建文件夹：{target}");
					break;
				case "create_docx":
					if (!string.Equals(Path.GetExtension(target), ".docx", StringComparison.OrdinalIgnoreCase))
					{
						target += ".docx";
					}
					PrepareNewFileTarget(target, roots);
					CreateMinimalDocx(target, action.Content ?? string.Empty);
					results.Add($"系统已创建 Word 文档：{target}");
					break;
				case "create_text_file":
					if (string.IsNullOrWhiteSpace(Path.GetExtension(target)))
					{
						target += ".txt";
					}
					PrepareNewFileTarget(target, roots);
					CreateTextFile(target, action.Content ?? string.Empty);
					results.Add($"系统已创建文本文件：{target}");
					break;
				case "open_item":
					results.Add(LaunchAssistantTarget(target));
					break;
				default:
					throw new InvalidOperationException("不支持的文件动作：" + action.Type);
				}
			}
			catch (Exception ex)
			{
				results.Add($"系统未执行“{action.Type}”：{ex.Message}");
			}
		}
		if (results.Count > 0 && Directory.Exists(_activePath))
		{
			LoadDirectory(_activePath);
		}
		return string.Join("\n", results);
	}

	private string LaunchAssistantTarget(string target)
	{
		if (!File.Exists(target) && !Directory.Exists(target))
		{
			throw new FileNotFoundException("条目已经移动、重命名或删除", target);
		}
		string displayName = Path.GetFileName(target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
		if (string.IsNullOrWhiteSpace(displayName))
		{
			displayName = target;
		}
		string? workingDirectory = Directory.Exists(target) ? target : Path.GetDirectoryName(target);
		long shellResult = ShellExecute(_windowHandle, "open", target, null, workingDirectory, 1).ToInt64();
		if (shellResult <= 32)
		{
			throw new InvalidOperationException($"Windows Shell 返回错误码 {shellResult}");
		}
		FooterStatusText.Text = $"LOG // OPEN REQUEST SENT · {displayName}";
		return $"系统已实际调用 Windows 打开入口：{displayName}";
	}

	private static void PrepareNewFileTarget(string target, IEnumerable<string> roots)
	{
		if (!IsAllowedPath(target, roots))
		{
			throw new UnauthorizedAccessException("目标不在 BIND FOLDERS 授权范围内");
		}
		if (File.Exists(target) || Directory.Exists(target))
		{
			throw new IOException("同名文件或文件夹已经存在，未覆盖原内容");
		}
		string? parent = Path.GetDirectoryName(target);
		if (string.IsNullOrWhiteSpace(parent) || !IsAllowedPath(parent, roots))
		{
			throw new UnauthorizedAccessException("目标目录不在授权范围内");
		}
		Directory.CreateDirectory(parent);
	}

	private static void CreateTextFile(string path, string content)
	{
		using FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
		using StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		writer.Write(content);
	}

	private static void CreateMinimalDocx(string path, string content)
	{
		try
		{
			using FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
			using ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);
			XNamespace contentTypes = "http://schemas.openxmlformats.org/package/2006/content-types";
			WriteXmlEntry(archive, "[Content_Types].xml", new XDocument(
				new XDeclaration("1.0", "utf-8", "yes"),
				new XElement(contentTypes + "Types",
					new XElement(contentTypes + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
					new XElement(contentTypes + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
					new XElement(contentTypes + "Override", new XAttribute("PartName", "/word/document.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml")))));

			XNamespace relationships = "http://schemas.openxmlformats.org/package/2006/relationships";
			WriteXmlEntry(archive, "_rels/.rels", new XDocument(
				new XDeclaration("1.0", "utf-8", "yes"),
				new XElement(relationships + "Relationships",
					new XElement(relationships + "Relationship", new XAttribute("Id", "rId1"), new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"), new XAttribute("Target", "word/document.xml")))));

			XNamespace word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
			IEnumerable<XElement> paragraphs = string.IsNullOrEmpty(content)
				? new[] { new XElement(word + "p") }
				: content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select((string line) =>
					new XElement(word + "p", new XElement(word + "r", new XElement(word + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), line))));
			XElement sectionProperties = new XElement(word + "sectPr",
				new XElement(word + "pgSz", new XAttribute(word + "w", "11906"), new XAttribute(word + "h", "16838")),
				new XElement(word + "pgMar", new XAttribute(word + "top", "1440"), new XAttribute(word + "right", "1440"), new XAttribute(word + "bottom", "1440"), new XAttribute(word + "left", "1440")));
			XElement body = new XElement(word + "body", paragraphs, sectionProperties);
			XElement documentRoot = new XElement(word + "document", new XAttribute(XNamespace.Xmlns + "w", word.NamespaceName), body);
			WriteXmlEntry(archive, "word/document.xml", new XDocument(new XDeclaration("1.0", "utf-8", "yes"), documentRoot));
		}
		catch
		{
			if (File.Exists(path))
			{
				File.Delete(path);
			}
			throw;
		}
	}

	private static void WriteXmlEntry(ZipArchive archive, string entryName, XDocument document)
	{
		ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
		using Stream stream = entry.Open();
		using StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		document.Save(writer, SaveOptions.DisableFormatting);
	}

	private void SetMood(string mood)
	{
		string moodKey = mood.ToLowerInvariant() switch
		{
			"happy" => "happy", 
			"analyzing" => "analyzing", 
			"alert" => "alert", 
			_ => "idle", 
		};
		LoadCharacterMood(moodKey);
		string normalized = moodKey.ToUpperInvariant();
		ActiveStateText.Text = normalized;
		ActiveStateText.Foreground = ((normalized == "ALERT") ? (FindResource("Gold") as System.Windows.Media.Brush) : (FindResource("Cyan") as System.Windows.Media.Brush));
		DoubleAnimation pulse = new DoubleAnimation(1.0, (normalized == "ALERT") ? 1.03 : 1.015, new Duration(TimeSpan.FromMilliseconds(300L)))
		{
			AutoReverse = true
		};
		CharacterScale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
		CharacterScale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
	}

	private void LoadCharacterMood(string mood)
	{
		if (!(_characterMood == mood))
		{
			string filename = ((mood == "idle") ? "character-neutral.png" : ("character-" + mood + ".png"));
			ImageSource? moodImage = LoadImageAsset(filename) ?? LoadImageAsset("character.png");
			if (moodImage != null)
			{
				CharacterImage.Source = moodImage;
				CharacterGhostImage.Source = moodImage;
				CharacterImage.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.68, 1.0, new Duration(TimeSpan.FromMilliseconds(210.0)))
				{
					EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
				});
			}
			_characterMood = mood;
		}
	}

	private ImageSource? LoadImageAsset(string filename)
	{
		string externalPath = Path.Combine(_baseDirectory, "Assets", filename);
		try
		{
			BitmapImage bitmap = new BitmapImage();
			bitmap.BeginInit();
			bitmap.CacheOption = BitmapCacheOption.OnLoad;
			if (File.Exists(externalPath))
			{
				bitmap.UriSource = new Uri(externalPath, UriKind.Absolute);
			}
			else
			{
				System.Resources.ResourceManager embeddedResources = new System.Resources.ResourceManager("StellarisUI.g", typeof(MainWindow).Assembly);
				using Stream embeddedStream = embeddedResources.GetStream($"assets/{filename.ToLowerInvariant()}", CultureInfo.InvariantCulture)
					?? throw new FileNotFoundException("Embedded image asset was not found.", filename);
				bitmap.StreamSource = embeddedStream;
				bitmap.EndInit();
				bitmap.Freeze();
				embeddedResources.ReleaseAllResources();
				return bitmap;
			}
			bitmap.EndInit();
			bitmap.Freeze();
			return bitmap;
		}
		catch
		{
			return null;
		}
	}

	private void RemoveUnneededHud()
	{
		Grid activeGrid = FindVisualAncestor(ActiveFolderText, (Grid g) => g.RowDefinitions.Count == 4);
		if (activeGrid != null)
		{
			activeGrid.RowDefinitions[0].Height = new GridLength(0.0);
		}
		FindVisualAncestor<Border>(ActiveFolderText)?.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Collapsed);
		FindVisualAncestor<Border>(FindTextBlock("SAFE SCOPE"))?.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Collapsed);
	}

	private TextBlock? FindTextBlock(string text)
	{
		return FindVisualDescendants(this).OfType<TextBlock>().FirstOrDefault((TextBlock block) => string.Equals(block.Text, text, StringComparison.Ordinal));
	}

	private static IEnumerable<DependencyObject> FindVisualDescendants(DependencyObject root)
	{
		for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
		{
			DependencyObject child = VisualTreeHelper.GetChild(root, index);
			yield return child;
			foreach (DependencyObject item in FindVisualDescendants(child))
			{
				yield return item;
			}
		}
	}

	private static T? FindVisualAncestor<T>(DependencyObject? child, Func<T, bool>? predicate = null) where T : DependencyObject
	{
		while (child != null)
		{
			child = VisualTreeHelper.GetParent(child);
			if (child is T match && (predicate == null || predicate(match)))
			{
				return match;
			}
		}
		return null;
	}

	private void AddChatLine(string label, string text, bool assistant)
	{
		ChatLine line = new ChatLine(label, text, assistant ? ((FindResource("Cyan") as System.Windows.Media.Brush) ?? System.Windows.Media.Brushes.White) : ((FindResource("Gold") as System.Windows.Media.Brush) ?? System.Windows.Media.Brushes.White), assistant ? ((FindResource("Silver") as System.Windows.Media.Brush) ?? System.Windows.Media.Brushes.White) : ((FindResource("Muted") as System.Windows.Media.Brush) ?? System.Windows.Media.Brushes.White));
		_chatLines.Add(line);
		ChatHistory.ScrollIntoView(line);
		PersistChatHistory();
	}

	private void ArrangeCommandInput()
	{
		Grid stage = FindVisualAncestor(ChatInput, (Grid grid) => grid.RowDefinitions.Count == 2 && grid.ColumnDefinitions.Count == 0);
		Border explanation = FindVisualAncestor<Border>(AssistantMessage);
		Border inputBorder = FindVisualAncestor<Border>(ChatInput);
		if (stage == null || explanation == null || inputBorder == null)
		{
			return;
		}
		stage.RowDefinitions.Add(new RowDefinition
		{
			Height = GridLength.Auto
		});
		Grid.SetRow(explanation, 1);
		Grid.SetRow(inputBorder, 2);
		inputBorder.Margin = new Thickness(6.0, 8.0, 6.0, 0.0);
		inputBorder.VerticalAlignment = VerticalAlignment.Top;
		if (VisualTreeHelper.GetParent(ChatInput) is Grid inputGrid)
		{
			System.Windows.Controls.Button send = FindVisualDescendants(inputBorder).OfType<System.Windows.Controls.Button>().FirstOrDefault((System.Windows.Controls.Button button) => string.Equals(button.Content?.ToString(), "SEND  ↵", StringComparison.Ordinal));
			if (send != null)
			{
				inputGrid.Children.Remove(send);
			}
			inputGrid.ColumnDefinitions.Clear();
			inputGrid.ColumnDefinitions.Add(new ColumnDefinition
			{
				Width = GridLength.Auto
			});
			inputGrid.ColumnDefinitions.Add(new ColumnDefinition
			{
				Width = new GridLength(1.0, GridUnitType.Star)
			});
			inputGrid.ColumnDefinitions.Add(new ColumnDefinition
			{
				Width = GridLength.Auto
			});
			TextBlock prompt = new TextBlock
			{
				Text = ">_",
				FontFamily = new System.Windows.Media.FontFamily("Consolas"),
				FontSize = 12.0,
				Foreground = (FindResource("Cyan") as System.Windows.Media.Brush),
				VerticalAlignment = VerticalAlignment.Center,
				Margin = new Thickness(4.0, 0.0, 8.0, 0.0)
			};
			Grid.SetColumn(prompt, 0);
			Grid.SetColumn(ChatInput, 1);
			ChatInput.ToolTip = "输入指令，按 Enter 发送";
			inputGrid.Children.Insert(0, prompt);
			System.Windows.Controls.Button imeInput = new System.Windows.Controls.Button
			{
				Style = (FindResource("TerminalButton") as Style),
				Content = "中 INPUT",
				Margin = new Thickness(8.0, 0.0, 0.0, 0.0),
				Padding = new Thickness(10.0, 6.0, 10.0, 6.0)
			};
			Grid.SetColumn(imeInput, 2);
			imeInput.Click += delegate
			{
				ShowTextEntryDialog(ChatInput, "灰风指令输入");
			};
			inputGrid.Children.Add(imeInput);
		}
	}

	private void LoadChatHistory()
	{
		try
		{
			string path = Path.Combine(_dataDirectory, "chat.json");
			if (!File.Exists(path))
			{
				return;
			}
			foreach (ChatRecord item in (JsonSerializer.Deserialize<List<ChatRecord>>(File.ReadAllText(path), JsonOptions) ?? new List<ChatRecord>()).TakeLast(30))
			{
				_chatLines.Add(new ChatLine(item.Label, item.Text, (item.Label == "灰风") ? ((FindResource("Cyan") as System.Windows.Media.Brush) ?? System.Windows.Media.Brushes.White) : ((FindResource("Gold") as System.Windows.Media.Brush) ?? System.Windows.Media.Brushes.White), (item.Label == "灰风") ? ((FindResource("Silver") as System.Windows.Media.Brush) ?? System.Windows.Media.Brushes.White) : ((FindResource("Muted") as System.Windows.Media.Brush) ?? System.Windows.Media.Brushes.White)));
			}
		}
		catch
		{
		}
	}

	private void PersistChatHistory()
	{
		try
		{
			File.WriteAllText(Path.Combine(_dataDirectory, "chat.json"), JsonSerializer.Serialize(from c in _chatLines.TakeLast(30)
				select new ChatRecord(c.Label, c.Text), JsonOptions));
		}
		catch
		{
		}
	}

	private void CancelCodex()
	{
		_codexCancellation?.Cancel();
		try
		{
			Process codexProcess = _codexProcess;
			if (codexProcess != null && !codexProcess.HasExited)
			{
				_codexProcess.Kill(entireProcessTree: true);
			}
		}
		catch
		{
		}
	}

	private static string ResolveCodex()
	{
		string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
		try
		{
			string found = (Directory.Exists(root) ? Directory.EnumerateFiles(root, "codex.exe", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() : null);
			if (found != null)
			{
				return found;
			}
		}
		catch
		{
		}
		return "codex";
	}

	private static string FindProjectDirectory(string runtimeDirectory)
	{
		DirectoryInfo directory = new DirectoryInfo(runtimeDirectory);
		int i = 0;
		while (i < 6 && directory != null)
		{
			if (File.Exists(Path.Combine(directory.FullName, "StellarisUI.csproj")))
			{
				return directory.FullName;
			}
			i++;
			directory = directory.Parent;
		}
		return runtimeDirectory;
	}

	private void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
	{
		FileItem? item = null;
		if (e.OriginalSource is DependencyObject source)
		{
			item = (ItemsControl.ContainerFromElement(FileList, source) as ListBoxItem)?.DataContext as FileItem;
		}
		item ??= FileList.SelectedItem as FileItem;
		if (item != null)
		{
			FileList.SelectedItem = item;
			if (item.IsDirectory)
			{
				NavigateToDirectory(item.FullPath);
			}
			else
			{
				OpenPath(item.FullPath);
			}
			e.Handled = true;
		}
	}

	private void NavigateToDirectory(string path)
	{
		try
		{
			if (_activeBinding == null || !Directory.Exists(path) || !IsAllowedPath(path, new[] { _activeBinding.Path }))
			{
				throw new UnauthorizedAccessException("目标文件夹不在当前模块的绑定范围内。");
			}
			_activePath = Path.GetFullPath(path);
			ActivePathText.Text = _activePath;
			SearchBox.Clear();
			DisposeWatcher();
			LoadDirectory(_activePath);
			TryWatch(_activePath);
			string folderName = Path.GetFileName(_activePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
			FooterStatusText.Text = $"LOG // ENTERED · {folderName}";
			AssistantMessage.Text = $"已进入“{folderName}”。点击 ↑ UP 返回上一级。";
		}
		catch (Exception ex)
		{
			FooterStatusText.Text = "LOG // FOLDER NAVIGATION FAILED";
			AssistantMessage.Text = $"无法进入该文件夹。\n{ex.Message}";
		}
	}

	private void NavigateUpButton_Click(object sender, RoutedEventArgs e)
	{
		if (_activeBinding == null || !Directory.Exists(_activeBinding.Path))
		{
			return;
		}
		string root = Path.GetFullPath(_activeBinding.Path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		string current = Path.GetFullPath(_activePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		if (string.Equals(root, current, StringComparison.OrdinalIgnoreCase))
		{
			FooterStatusText.Text = "LOG // MODULE ROOT";
			return;
		}
		DirectoryInfo? parent = Directory.GetParent(current);
		if (parent != null && IsAllowedPath(parent.FullName, new[] { root }))
		{
			NavigateToDirectory(parent.FullName);
		}
	}

	private void OpenPath(string path)
	{
		string displayName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
		if (string.IsNullOrWhiteSpace(displayName))
		{
			displayName = path;
		}
		try
		{
			if (Directory.Exists(path))
			{
				NavigateToDirectory(path);
				return;
			}
			else
			{
				bool shellLocation = path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase);
				if (!shellLocation && !File.Exists(path))
				{
					throw new FileNotFoundException("文件已经移动、重命名或删除。", path);
				}

				string? workingDirectory = shellLocation ? null : Path.GetDirectoryName(path);
				long shellResult = ShellExecute(_windowHandle, "open", path, null, workingDirectory, 1).ToInt64();
				if (shellResult <= 32)
				{
					if (!shellLocation && IsBrowserViewable(path) && TryOpenWithBrowser(path, out string browserName))
					{
						FooterStatusText.Text = $"LOG // OPENED VIA {browserName.ToUpperInvariant()} · {displayName}";
						return;
					}
					if (!shellLocation && TryShowOpenWithDialog(path))
					{
						FooterStatusText.Text = $"LOG // OPEN WITH · {displayName}";
						return;
					}
					throw new InvalidOperationException($"Windows Shell 返回错误码 {shellResult}。");
				}
			}

			FooterStatusText.Text = $"LOG // OPENED · {displayName}";
		}
		catch (Exception ex)
		{
			FooterStatusText.Text = $"LOG // OPEN FAILED · {displayName}";
			AssistantMessage.Text = $"无法打开“{displayName}”。\n{ex.Message}\n请检查默认打开程序或文件权限。";
		}
	}

	private static void StartExecutable(string executablePath, params string[] arguments)
	{
		if (!File.Exists(executablePath))
		{
			throw new FileNotFoundException("找不到用于打开条目的系统程序。", executablePath);
		}
		ProcessStartInfo startInfo = new ProcessStartInfo
		{
			FileName = executablePath,
			UseShellExecute = false,
			WorkingDirectory = Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory
		};
		foreach (string argument in arguments)
		{
			startInfo.ArgumentList.Add(argument);
		}
		Process.Start(startInfo);
	}

	private static bool IsBrowserViewable(string path)
	{
		return Path.GetExtension(path).ToLowerInvariant() switch
		{
			".pdf" or ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" or ".svg" or ".htm" or ".html" or ".mp4" or ".webm" or ".mp3" or ".wav" => true,
			_ => false
		};
	}

	private static bool TryOpenWithBrowser(string path, out string browserName)
	{
		string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
		string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
		string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		(string Name, string Path)[] candidates =
		{
			("Microsoft Edge", Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe")),
			("Microsoft Edge", Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe")),
			("Microsoft Edge", Path.Combine(localAppData, "Microsoft", "Edge", "Application", "msedge.exe")),
			("Google Chrome", Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe")),
			("Google Chrome", Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe")),
			("Google Chrome", Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe"))
		};
		foreach ((string name, string executablePath) in candidates.Distinct())
		{
			if (!File.Exists(executablePath))
			{
				continue;
			}
			try
			{
				StartExecutable(executablePath, path);
				browserName = name;
				return true;
			}
			catch
			{
			}
		}
		browserName = string.Empty;
		return false;
	}

	private static bool TryShowOpenWithDialog(string path)
	{
		try
		{
			string rundll32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "rundll32.exe");
			StartExecutable(rundll32, "shell32.dll,OpenAs_RunDLL", path);
			return true;
		}
		catch
		{
			return false;
		}
	}

	private void RecycleButton_Click(object sender, RoutedEventArgs e)
	{
		OpenPath("shell:RecycleBinFolder");
	}

	private void RefreshButton_Click(object sender, RoutedEventArgs e)
	{
		if (Directory.Exists(_activePath))
		{
			LoadDirectory(_activePath);
		}
		UpdateSystemStatus();
	}

	private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
	{
		ApplyFileFilter();
	}

	private void SearchImeButton_Click(object sender, RoutedEventArgs e)
	{
		ShowTextEntryDialog(SearchBox, "中文搜索输入");
	}

	private void TextInput_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
	{
		if (sender is System.Windows.Controls.TextBox textBox)
		{
			InputMethod.SetIsInputMethodEnabled(textBox, true);
			try
			{
				InputMethod.Current.ImeState = InputMethodState.On;
			}
			catch
			{
			}
		}
		Activate();
		if (_windowHandle != IntPtr.Zero)
		{
			SetWindowPos(_windowHandle, IntPtr.Zero, 0, 0, 0, 0, 67u);
		}
	}

	private void TextInput_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
	{
		if (!_textEntryDialogOpen)
		{
			ScheduleDesktopLayerSync();
		}
	}

	private void ShowTextEntryDialog(System.Windows.Controls.TextBox target, string title)
	{
		_textEntryDialogOpen = true;
		Window dialog = new Window
		{
			Owner = this,
			Title = title,
			Width = 620.0,
			SizeToContent = SizeToContent.Height,
			WindowStartupLocation = WindowStartupLocation.CenterOwner,
			ResizeMode = ResizeMode.NoResize,
			WindowStyle = WindowStyle.ToolWindow,
			ShowInTaskbar = false,
			Topmost = true,
			Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(9, 15, 22)),
			Foreground = System.Windows.Media.Brushes.White
		};
		Grid root = new Grid
		{
			Margin = new Thickness(18.0)
		};
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		System.Windows.Controls.TextBox editor = new System.Windows.Controls.TextBox
		{
			Text = target.Text,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI, Microsoft YaHei UI"),
			FontSize = 16.0,
			Foreground = System.Windows.Media.Brushes.White,
			Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(18, 29, 39)),
			BorderBrush = FindResource("Cyan") as System.Windows.Media.Brush,
			BorderThickness = new Thickness(1.0),
			Padding = new Thickness(12.0, 9.0, 12.0, 9.0),
			Language = XmlLanguage.GetLanguage("zh-CN")
		};
		InputMethod.SetIsInputMethodEnabled(editor, true);
		InputMethod.SetPreferredImeState(editor, InputMethodState.On);
		root.Children.Add(editor);
		StackPanel actions = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Horizontal,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
			Margin = new Thickness(0.0, 14.0, 0.0, 0.0)
		};
		Grid.SetRow(actions, 1);
		System.Windows.Controls.Button cancel = new System.Windows.Controls.Button
		{
			Style = FindResource("TerminalButton") as Style,
			Content = "CANCEL",
			Margin = new Thickness(0.0, 0.0, 8.0, 0.0)
		};
		System.Windows.Controls.Button apply = new System.Windows.Controls.Button
		{
			Style = FindResource("TerminalButton") as Style,
			Content = "APPLY",
			Foreground = FindResource("Cyan") as System.Windows.Media.Brush
		};
		cancel.Click += delegate { dialog.DialogResult = false; };
		apply.Click += delegate { dialog.DialogResult = true; };
		actions.Children.Add(cancel);
		actions.Children.Add(apply);
		root.Children.Add(actions);
		dialog.Content = root;
		dialog.Loaded += delegate
		{
			dialog.Activate();
			editor.Focus();
			Keyboard.Focus(editor);
			editor.CaretIndex = editor.Text.Length;
			try { InputMethod.Current.ImeState = InputMethodState.On; } catch { }
		};
		try
		{
			if (dialog.ShowDialog() == true)
			{
				target.Text = editor.Text;
				target.CaretIndex = target.Text.Length;
				target.Focus();
			}
		}
		finally
		{
			_textEntryDialogOpen = false;
			ScheduleDesktopLayerSync();
		}
	}

	private void HideButton_Click(object sender, RoutedEventArgs e)
	{
		HideOverlay();
	}

	private void ExitButton_Click(object sender, RoutedEventArgs e)
	{
		Close();
	}

	private void ChatInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
	{
		if (e.Key == Key.Return)
		{
			e.Handled = true;
			SubmitChat();
		}
		else if (e.Key == Key.Escape)
		{
			Process codexProcess = _codexProcess;
			if (codexProcess != null && !codexProcess.HasExited)
			{
				e.Handled = true;
				CancelCodex();
			}
		}
	}

	private void SendButton_Click(object sender, RoutedEventArgs e)
	{
		SubmitChat();
	}

	private void ChatImeButton_Click(object sender, RoutedEventArgs e)
	{
		ShowTextEntryDialog(ChatInput, "灰风指令输入");
	}

	private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
	{
		if (e.Key == Key.F10)
		{
			e.Handled = true;
			ToggleOverlay();
		}
		else if (e.Key == Key.F5)
		{
			e.Handled = true;
			RefreshButton_Click(sender, e);
		}
		else if (e.Key == Key.Escape && SearchBox.IsKeyboardFocusWithin)
		{
			SearchBox.Clear();
		}
	}

	private void HideOverlay()
	{
		Hide();
	}

	private void ToggleOverlay()
	{
		if (base.IsVisible)
		{
			Hide();
			return;
		}
		Show();
		base.WindowState = WindowState.Maximized;
		AnchorDesktopLayer();
	}

	private void ShowFromTray()
	{
		Show();
		base.WindowState = WindowState.Maximized;
		AnchorDesktopLayer();
	}

	private void InitializeTrayIcon()
	{
		ContextMenuStrip menu = new ContextMenuStrip();
		menu.Items.Add("显示指挥终端", null, delegate
		{
			ShowFromTray();
		});
		menu.Items.Add("隐藏指挥终端", null, delegate
		{
			HideOverlay();
		});
		menu.Items.Add(new ToolStripSeparator());
		menu.Items.Add("退出", null, delegate
		{
			Close();
		});
		_trayIcon = new NotifyIcon
		{
			Icon = SystemIcons.Application,
			Text = "灰风 · 纳米指挥终端",
			ContextMenuStrip = menu,
			Visible = true
		};
		_trayIcon.DoubleClick += delegate
		{
			ShowFromTray();
		};
	}

	private void RegisterHotkey()
	{
		_windowHandle = new WindowInteropHelper(this).Handle;
		_source = HwndSource.FromHwnd(_windowHandle);
		_source?.AddHook(HwndHook);
		RegisterHotKey(_windowHandle, 18960, 16384u, 121u);
		_winEventDelegate = OnWinEvent;
		_foregroundHook = SetWinEventHook(3u, 3u, IntPtr.Zero, _winEventDelegate, 0u, 0u, 2u);
		AnchorDesktopLayer();
	}

	private nint HwndHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
	{
		switch (msg)
		{
		case 786:
			if (((IntPtr)wParam).ToInt32() == 18960)
			{
				ToggleOverlay();
				handled = true;
			}
			break;
		}
		return IntPtr.Zero;
	}

	private void AnchorDesktopLayer()
	{
		bool textInputActive = _textEntryDialogOpen || ChatInput?.IsKeyboardFocusWithin == true || SearchBox?.IsKeyboardFocusWithin == true;
		if (_windowHandle != IntPtr.Zero && base.IsVisible && !textInputActive)
		{
			nint desktopAnchor = FindWindow("Progman", "Program Manager");
			if (desktopAnchor == IntPtr.Zero)
			{
				desktopAnchor = FindWindow("WorkerW", null);
			}
			if (desktopAnchor != IntPtr.Zero)
			{
				SetWindowPos(_windowHandle, desktopAnchor, 0, 0, 0, 0, 83u);
			}
		}
	}

	private void ScheduleDesktopLayerSync()
	{
		if (!_closing)
		{
			base.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(AnchorDesktopLayer));
		}
	}

	private void OnWinEvent(nint hook, uint eventType, nint hwnd, int objectId, int childId, uint eventThreadId, uint eventTime)
	{
		if (!_closing && hwnd != _windowHandle)
		{
			base.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(AnchorDesktopLayer));
		}
	}

	internal void RestorePreviousForeground(nint previousForeground)
	{
		if (_windowHandle != IntPtr.Zero)
		{
			if (previousForeground != IntPtr.Zero && previousForeground != _windowHandle)
			{
				SetForegroundWindow(previousForeground);
			}
			AnchorDesktopLayer();
		}
	}

	private void DisposeResources()
	{
		_closing = true;
		CancelCodex();
		DisposeWatcher();
		_clockTimer?.Stop();
		_weatherTimer?.Stop();
		_blinkTimer?.Stop();
		_weatherClient.Dispose();
		if (_windowHandle != IntPtr.Zero)
		{
			UnregisterHotKey(_windowHandle, 18960);
		}
		if (_foregroundHook != IntPtr.Zero)
		{
			UnhookWinEvent(_foregroundHook);
		}
		_foregroundHook = IntPtr.Zero;
		_source?.RemoveHook(HwndHook);
		_trayIcon?.Dispose();
		_trayIcon = null;
		_driveMenu?.Items.Clear();
		_driveMenu = null;
	}

	private static bool IsAllowedPath(string candidate, IEnumerable<string> roots)
	{
		return roots.Any((string root) => string.Equals(Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) || Path.GetFullPath(candidate).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
	}

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool UnregisterHotKey(nint hWnd, int id);

	[DllImport("user32.dll")]
	private static extern bool SetForegroundWindow(nint hWnd);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern nint FindWindow(string? lpClassName, string? lpWindowName);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

	[DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern nint ShellExecute(nint hwnd, string? operation, string file, string? parameters, string? directory, int showCommand);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint flags);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool UnhookWinEvent(nint hWinEventHook);
}
