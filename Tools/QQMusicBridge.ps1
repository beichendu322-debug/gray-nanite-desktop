param(
    [string]$Command = 'status',
    [string]$Value = ''
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

function Await-WinRt {
    param($Operation, [Type]$ResultType)
    $method = [System.WindowsRuntimeSystemExtensions].GetMethods() |
        Where-Object { $_.Name -eq 'AsTask' -and $_.IsGenericMethodDefinition -and $_.GetParameters().Count -eq 1 } |
        Select-Object -First 1
    $task = $method.MakeGenericMethod($ResultType).Invoke($null, @($Operation))
    $task.Wait()
    return $task.Result
}

function Send-MediaKey {
    param([byte]$Key)
    if (-not ('GrayWind.MediaKeys' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace GrayWind {
    public static class MediaKeys {
        [DllImport("user32.dll")]
        private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
        public static void Send(byte key) {
            keybd_event(key, 0, 0, UIntPtr.Zero);
            keybd_event(key, 0, 2, UIntPtr.Zero);
        }
    }
}
'@
    }
    [GrayWind.MediaKeys]::Send($Key)
}

function Write-Result($Value) {
    $Value | ConvertTo-Json -Compress -Depth 5
}

try {
    Add-Type -AssemblyName System.Runtime.WindowsRuntime
    $managerType = [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager,Windows.Media.Control,ContentType=WindowsRuntime]
    $manager = Await-WinRt ($managerType::RequestAsync()) $managerType
    $session = $manager.GetSessions() | Where-Object {
        $_.SourceAppUserModelId -match '(?i)qqmusic|tencent'
    } | Select-Object -First 1

    if ($null -eq $session) {
        throw 'QQ Music has not exposed a media session yet.'
    }

    switch ($Command.ToLowerInvariant()) {
        'status' {
            $info = $session.GetPlaybackInfo()
            $timeline = $session.GetTimelineProperties()
            $propertiesType = [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties,Windows.Media.Control,ContentType=WindowsRuntime]
            $media = $null
            try { $media = Await-WinRt ($session.TryGetMediaPropertiesAsync()) $propertiesType } catch { }
            $repeat = if ($info.AutoRepeatMode.HasValue) { $info.AutoRepeatMode.Value.ToString() } else { 'None' }
            Write-Result ([ordered]@{
                available = $true
                source = $session.SourceAppUserModelId
                title = if ($null -eq $media) { '' } else { $media.Title }
                artist = if ($null -eq $media) { '' } else { $media.Artist }
                status = $info.PlaybackStatus.ToString()
                position = $timeline.Position.TotalSeconds
                duration = $timeline.EndTime.TotalSeconds
                seekable = [bool]$info.Controls.IsPlaybackPositionEnabled
                repeat = $repeat
                shuffle = ($info.IsShuffleActive.HasValue -and $info.IsShuffleActive.Value)
            })
        }
        'play' { Write-Result @{ ok = [bool](Await-WinRt ($session.TryPlayAsync()) ([bool])) } }
        'pause' { Write-Result @{ ok = [bool](Await-WinRt ($session.TryPauseAsync()) ([bool])) } }
        'playpause' {
            if ($session.GetPlaybackInfo().PlaybackStatus.ToString() -eq 'Playing') {
                Write-Result @{ ok = [bool](Await-WinRt ($session.TryPauseAsync()) ([bool])) }
            } else {
                Write-Result @{ ok = [bool](Await-WinRt ($session.TryPlayAsync()) ([bool])) }
            }
        }
        'next' { Write-Result @{ ok = [bool](Await-WinRt ($session.TrySkipNextAsync()) ([bool])) } }
        'previous' { Write-Result @{ ok = [bool](Await-WinRt ($session.TrySkipPreviousAsync()) ([bool])) } }
        'seek' {
            $seconds = [Math]::Max(0, [double]::Parse($Value, [Globalization.CultureInfo]::InvariantCulture))
            if (-not $session.GetPlaybackInfo().Controls.IsPlaybackPositionEnabled) {
                Write-Result @{ ok = $false; error = 'QQ Music media session does not expose seeking.' }
                break
            }
            $ok = [bool](Await-WinRt ($session.TryChangePlaybackPositionAsync([TimeSpan]::FromSeconds($seconds).Ticks)) ([bool]))
            Write-Result @{ ok = $ok; error = if ($ok) { '' } else { 'QQ Music rejected the requested playback position.' } }
        }
        'mode' {
            $repeatType = [Windows.Media.MediaPlaybackAutoRepeatMode,Windows.Media,ContentType=WindowsRuntime]
            $shuffle = $Value -eq 'shuffle'
            $repeat = if ($Value -eq 'repeat-one') { $repeatType::Track } elseif ($Value -eq 'repeat-all') { $repeatType::List } else { $repeatType::None }
            $okShuffle = [bool](Await-WinRt ($session.TryChangeShuffleActiveAsync($shuffle)) ([bool]))
            $okRepeat = [bool](Await-WinRt ($session.TryChangeAutoRepeatModeAsync($repeat)) ([bool]))
            Write-Result @{ ok = ($okShuffle -and $okRepeat) }
        }
        default { Write-Result @{ ok = $false; error = 'unknown command' } }
    }
} catch {
    $key = switch ($Command.ToLowerInvariant()) {
        'next' { 0xB0 }
        'previous' { 0xB1 }
        'play' { 0xB3 }
        'pause' { 0xB3 }
        'playpause' { 0xB3 }
        default { 0 }
    }
    if ($key -ne 0) {
        Send-MediaKey ([byte]$key)
        Write-Result @{ ok = $true; fallback = $true; error = $_.Exception.Message }
    } else {
        Write-Result @{ available = $false; error = $_.Exception.Message }
    }
}
