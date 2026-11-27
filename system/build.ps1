$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$bin = Join-Path $root "bin"
New-Item -ItemType Directory -Force $bin | Out-Null
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { throw "csc.exe not found: $csc" }
$sources = Get-ChildItem (Join-Path $root "src") -Filter *.cs | ForEach-Object { $_.FullName }
$out = Join-Path $bin "DSHVoiceSystem.exe"
$icon = Join-Path $root "icon.ico"
$iconArg = @()
if (Test-Path $icon) { $iconArg = @("/win32icon:$icon") }
& $csc /nologo /target:winexe /optimize+ /out:$out `
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
  /r:System.Net.Http.dll /r:System.Web.Extensions.dll $iconArg $sources
if ($LASTEXITCODE -ne 0) { throw "build failed (exit $LASTEXITCODE)" }
Copy-Item (Join-Path $root "README.md") $bin -Force -ErrorAction SilentlyContinue
Write-Host "built: $out"
