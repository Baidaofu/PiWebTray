# 用 Windows PowerShell 5.1 自带的 csc 编译，无需安装任何 SDK
$ErrorActionPreference = 'Stop'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $dir 'PiWebTray.cs'
$out = Join-Path $dir 'PiWebTray.exe'

if (Test-Path $out) { Remove-Item $out -Force }

$refs = @(
    'System.dll',
    'System.Core.dll',
    'System.Drawing.dll',
    'System.Windows.Forms.dll'
)

$code = [System.IO.File]::ReadAllText($src, [System.Text.Encoding]::UTF8)

Add-Type -TypeDefinition $code `
    -Language CSharp `
    -ReferencedAssemblies $refs `
    -OutputAssembly $out `
    -OutputType WindowsApplication

Write-Host "built -> $out  ($((Get-Item $out).Length) bytes)"