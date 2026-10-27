# Builds dist\Stillbar-Setup-<version>.exe: tests, a self-contained publish (no .NET needed on the target
# machine), then Inno Setup. Run from the repository root:  powershell -File installer\build-installer.ps1
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found. Install it: winget install JRSoftware.InnoSetup' }

Write-Host '1/3 tests'
dotnet test Stillbar.slnx --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'tests failed' }

Write-Host '2/3 self-contained publish'
# A short folder: ISCC cannot read paths over 260 characters, and this repository is deep.
$app = Join-Path ([IO.Path]::GetTempPath()) 'stillbar-dist\app'
if (Test-Path $app) { Remove-Item $app -Recurse -Force }
dotnet publish src\Stillbar.App -c Release -r win-x64 --self-contained true -o $app -nologo -v q -p:DebugType=none
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

Write-Host '3/3 Inno Setup'
& $iscc /Qp "/DAppSource=$app" installer\Stillbar.iss
if ($LASTEXITCODE -ne 0) { throw 'ISCC failed' }

Get-ChildItem dist\Stillbar-Setup-*.exe | ForEach-Object { '{0}  {1:N1} MB' -f $_.FullName, ($_.Length / 1MB) }
