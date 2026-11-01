param(
    [string]$Dotnet = 'D:\ruanjian\dotnet\dotnet.exe',
    [switch]$Install,
    [string]$InstallDirectory = 'D:\ruanjian\VitroHUD'
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $Dotnet)) { $Dotnet = (Get-Command dotnet.exe -ErrorAction Stop).Source }
$payload = Join-Path $PSScriptRoot 'artifacts\VitroHUD'
Push-Location -LiteralPath $PSScriptRoot
try {
    & $Dotnet publish 'src\VitroHUD.Probe\VitroHUD.Probe.csproj' -c Release -r win-x64 --self-contained true -o (Join-Path $payload 'probe') --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Probe publish failed.' }
    & $Dotnet publish 'src\VitroHUD.Desktop\VitroHUD.Desktop.csproj' -c Release -r win-x64 --self-contained true -o $payload --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }
    foreach ($required in 'resources.pri','App.xbf','MainWindow.xbf','Assets\VitroHUD.ico','Assets\VitroHUD.png','Assets\WidgetMark.png','probe\VitroHUD.Probe.exe') {
        if (-not (Test-Path -LiteralPath (Join-Path $payload $required))) { throw "Missing output: $required" }
    }
    & (Join-Path $payload 'probe\VitroHUD.Probe.exe') --self-test
    if ($LASTEXITCODE -ne 0) { throw 'Adapter guard checks failed.' }
    if ($Install) {
        New-Item -ItemType Directory -Path $InstallDirectory -Force | Out-Null
        Get-ChildItem -LiteralPath $payload -Force | Copy-Item -Destination $InstallDirectory -Recurse -Force
        Write-Output (Join-Path $InstallDirectory 'VitroHUD.exe')
    } else { Write-Output (Join-Path $payload 'VitroHUD.exe') }
} finally { Pop-Location }
