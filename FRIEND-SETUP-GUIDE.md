# Gray Nanite Desktop — friend setup

This guide is for a nontechnical Windows user. If the repository is private, the owner must first invite the friend as a GitHub collaborator.

## Give this prompt to ChatGPT or Codex

```text
Set up Gray Nanite Desktop on this Windows PC from:
https://github.com/beichendu322-debug/gray-nanite-desktop

Please do the setup yourself when computer/terminal access is available, and explain every action in simple language.

1. Confirm that GitHub access works, then clone the repository into a new folder under my user profile. Do not overwrite an existing folder.
2. Check for the .NET 10 SDK. If it is missing, open Microsoft's official .NET download page and let me approve the installation.
3. Run Build.ps1 from the repository. Stop and show me the exact error if the build fails.
4. Run publish\StellarisUI.exe --self-test and continue only if it succeeds.
5. Create a normal desktop shortcut to publish\StellarisUI.exe. Do not enable Windows startup unless I explicitly ask.
6. Check `codex --version`. If Codex is missing, open the official Codex CLI instructions. If login is needed, run `codex login` and let me finish the browser login myself.
7. Launch the app and remind me to use BIND FOLDERS once. F10 hides or shows the interface; the notification-area icon can also show, hide, or exit it.

Never ask me to paste auth.json, API keys, passwords, browser cookies, or GitHub tokens into chat. Do not change Codex sandbox or approval settings; the app inherits my existing settings.
```

## Manual fallback

Open PowerShell in the cloned repository and run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build.ps1
.\publish\StellarisUI.exe --self-test
.\publish\StellarisUI.exe
```

The app stays in the notification area, not the taskbar. Windows startup is optional and can be enabled later from `BIND FOLDERS`.
