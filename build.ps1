param(
    [switch]$NoIcon
)

$ErrorActionPreference = 'Stop'
$running = Get-Process Gestosh -ErrorAction SilentlyContinue
if ($running) {
    throw 'Gestosh is running. Choose Exit in its tray menu (or run Gestosh.exe --exit) before building.'
}
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path $csc)) {
    throw 'Cannot find the in-box C# compiler (csc.exe). Windows 10/11 with .NET Framework 4.x is required.'
}

$sources = Get-ChildItem (Join-Path $root 'src') -Filter '*.cs' | Sort-Object Name | ForEach-Object { $_.FullName }
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
$version = '1.4.0.0'

$icon = Join-Path $root 'assets\Gestosh.ico'
$manifest = Join-Path $root 'src\Gestosh.manifest'
# /codepage:65001 keeps the UTF-8 sources (comments and the localized assembly
# description) readable even though the in-box csc would otherwise fall back to
# the system ANSI code page for files without a BOM.
$common = @('/nologo', '/optimize+', '/platform:anycpu', '/codepage:65001')
if (-not $NoIcon -and (Test-Path $icon)) { $common += '/win32icon:' + $icon }
if (Test-Path $manifest) { $common += '/win32manifest:' + $manifest }

Write-Host 'Building Gestosh.exe (GUI)...'
$gui = @('/target:winexe', ('/out:' + (Join-Path $dist 'Gestosh.exe'))) + $common + $sources
& $csc @gui
if ($LASTEXITCODE -ne 0) { throw 'GUI build failed.' }

Write-Host 'Building Gestosh.Debug.exe (console diagnostics)...'
$debug = @('/target:exe', '/define:CONSOLE_BUILD', ('/out:' + (Join-Path $dist 'Gestosh.Debug.exe'))) + $common + $sources
& $csc @debug
if ($LASTEXITCODE -ne 0) { throw 'Debug build failed.' }

# The in-box compiler can only emit one language-neutral VERSIONINFO block, and it
# refuses /win32res together with /win32icon and /win32manifest. The version
# resource is therefore rewritten in place after linking, using a staged copy so a
# failed update can never leave dist without a working executable.
Write-Host 'Writing version resource...'
$obj = Join-Path $root 'obj'
New-Item -ItemType Directory -Force -Path $obj | Out-Null
$patcher = Join-Path $obj 'PatchVersion.exe'
& $csc /nologo ('/out:' + $patcher) (Join-Path $root 'tools\PatchVersion.cs')
if ($LASTEXITCODE -ne 0) { throw 'PatchVersion build failed.' }
foreach ($name in @('Gestosh.exe', 'Gestosh.Debug.exe')) {
    $target = Join-Path $dist $name
    $staging = Join-Path $obj ($name + '.staged')
    Copy-Item $target $staging -Force
    & $patcher $staging $version
    if ($LASTEXITCODE -ne 0) {
        Remove-Item $staging -Force -ErrorAction SilentlyContinue
        throw "Version patch failed for $name"
    }
    $check = (Get-Item $staging).VersionInfo
    if ($check.FileVersion -ne $version -or [string]::IsNullOrEmpty($check.FileDescription)) {
        Remove-Item $staging -Force -ErrorAction SilentlyContinue
        throw "Version patch verification failed for $name"
    }
    Move-Item -Force $staging $target
}

$config = Join-Path $root 'config\Gestosh.ini'
if (Test-Path $config) {
    Copy-Item $config (Join-Path $dist 'Gestosh.ini') -Force
}
# A Mark-of-the-Web on the freshly built files would make Explorer and SmartScreen
# treat them as downloaded from the Internet. The build output is local, so clear it.
Get-ChildItem $dist -Filter '*.exe' | ForEach-Object { Unblock-File -LiteralPath $_.FullName -ErrorAction SilentlyContinue }

Write-Host ''
Write-Host 'Done:'
Get-ChildItem $dist | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize
Get-ChildItem $dist -Filter '*.exe' | ForEach-Object {
    $info = $_.VersionInfo
    [pscustomobject]@{
        Name        = $_.Name
        FileVersion = $info.FileVersion
        Product     = $info.ProductName
        Company     = $info.CompanyName
        Description = $info.FileDescription
        Language    = $info.Language
    }
} | Format-List
