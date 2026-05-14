$ErrorActionPreference = "Stop"
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path -LiteralPath $csc)) {
  throw "Could not find .NET Framework C# compiler at $csc"
}

$icon = Join-Path $PSScriptRoot "CLI.ico"
$iconArg = if (Test-Path -LiteralPath $icon) { "/win32icon:$icon" } else { "" }

& $csc /nologo /target:winexe /out:CLI.exe $iconArg /reference:System.Windows.Forms.dll /reference:System.Drawing.dll NativeTerminalHost.cs
if ($LASTEXITCODE -ne 0) {
  exit $LASTEXITCODE
}

"Built C:\IDE\CLI.exe"
