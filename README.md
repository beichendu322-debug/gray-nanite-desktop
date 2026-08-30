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
