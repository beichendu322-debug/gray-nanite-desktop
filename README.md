# Gray Nanite Desktop v11.0 source

Windows WPF application targeting `net10.0-windows`.

Main files:

- `StellarisUI.MainWindow.xaml` — terminal layout, QQ Music dock and holographic effects.
- `StellarisUI/MainWindow.cs` — desktop shell, file actions, chat, animation and music control logic.
- `StellarisUI/QQMusicAudio.cs` — per-process QQ Music volume control through Windows Core Audio.
- `Tools/QQMusicBridge.ps1` — Windows system-media-session bridge for title, progress, seek and playback mode.
- `Assets/` — embedded character and background resources.

Build with `Build.ps1`, or run:

```powershell
dotnet publish .\StellarisUI.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

The QQ Music bridge uses the media session exposed by the installed QQ Music client. No account cookies, passwords or private QQ Music files are read.

The overlay stays below normal application windows and is controlled through F10 or its notification-area icon; it does not create a taskbar button. Windows startup is opt-in under `BIND FOLDERS`.

Gray's chat uses the local Codex CLI and inherits the user's Codex sandbox and approval configuration. Structured file actions remain limited to bound folders, reject junction/symlink escapes, never overwrite existing entries, and ask before launching executable content. Folder categorization supports destinations on another disk.

For a nontechnical installation, follow [`FRIEND-SETUP-GUIDE.md`](FRIEND-SETUP-GUIDE.md).

