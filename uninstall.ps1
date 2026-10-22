param(
    [switch]$Purge
)

$ErrorActionPreference = 'Stop'
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\Gestosh'
$exe = Join-Path $installDir 'Gestosh.exe'
$startup = [Environment]::GetFolderPath('Startup')
$shortcutPath = Join-Path $startup 'Gestosh.lnk'

if (Test-Path $exe) {
    try { & $exe --exit | Out-Null } catch { }
    for ($i = 0; $i -lt 20; $i++) {
        if (-not (Get-Process Gestosh -ErrorAction SilentlyContinue)) { break }
        Start-Sleep -Milliseconds 100
    }
}
if (Test-Path $shortcutPath) { Remove-Item $shortcutPath -Force }
if (Test-Path $installDir) { Remove-Item $installDir -Recurse -Force }

# If a crash left the Windows three-/four-finger gestures disabled, put them back.
try {
    $marker = 'HKCU:\Software\Gestosh'
    if (Test-Path $marker) {
        $state = Get-ItemProperty -Path $marker -ErrorAction SilentlyContinue
        $touchpad = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad'
        if ($state -and $state.Managed -eq 1 -and (Test-Path $touchpad)) {
            if ($state.OriginalExists -eq 1) {
                Set-ItemProperty -Path $touchpad -Name 'ThreeFingerSlideEnabled' -Value ([int]$state.OriginalValue) -Type DWord
            } else {
                Remove-ItemProperty -Path $touchpad -Name 'ThreeFingerSlideEnabled' -ErrorAction SilentlyContinue
            }
        }
        if ($state -and $state.FourManaged -eq 1 -and (Test-Path $touchpad)) {
            if ($state.FourOriginalExists -eq 1) {
                Set-ItemProperty -Path $touchpad -Name 'FourFingerSlideEnabled' -Value ([int]$state.FourOriginalValue) -Type DWord
            } else {
                Remove-ItemProperty -Path $touchpad -Name 'FourFingerSlideEnabled' -ErrorAction SilentlyContinue
            }
        }
        Remove-Item $marker -Recurse -Force -ErrorAction SilentlyContinue
    }
} catch { }

if ($Purge) {
    $config = Join-Path $env:APPDATA 'Gestosh'
    $logs = Join-Path $env:LOCALAPPDATA 'Gestosh'
    if (Test-Path $config) { Remove-Item $config -Recurse -Force }
    if (Test-Path $logs) { Remove-Item $logs -Recurse -Force }
}
Write-Host 'Gestosh removed.'
