param(
    [switch]$NoStartup,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = Join-Path $root 'dist\Gestosh.exe'
if (-not (Test-Path $source)) { throw 'Build first: powershell -ExecutionPolicy Bypass -File build.ps1' }

$installDir = Join-Path $env:LOCALAPPDATA 'Programs\Gestosh'
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
Copy-Item $source (Join-Path $installDir 'Gestosh.exe') -Force
# A copy made from a downloaded file keeps its Mark-of-the-Web, which makes
# SmartScreen treat the installed program as untrusted. The build output is local.
Unblock-File -LiteralPath (Join-Path $installDir 'Gestosh.exe') -ErrorAction SilentlyContinue
$configSource = Join-Path $root 'dist\Gestosh.ini'
$configTarget = Join-Path $installDir 'Gestosh.ini'
if ((Test-Path $configSource) -and -not (Test-Path $configTarget)) {
    Copy-Item $configSource $configTarget
}

$exe = Join-Path $installDir 'Gestosh.exe'
if (-not $NoStartup) {
    $startup = [Environment]::GetFolderPath('Startup')
    $shortcutPath = Join-Path $startup 'Gestosh.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $exe
    $shortcut.WorkingDirectory = $installDir
    $shortcut.Description = 'Gestosh - precision touchpad gestures'
    $shortcut.Save()
    Write-Host "Startup shortcut: $shortcutPath"
}

Write-Host "Installed: $exe"
Write-Host "Config:    $configTarget"
if (-not $NoLaunch) {
    Start-Process -FilePath $exe
    Write-Host 'Gestosh started.'
}
