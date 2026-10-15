# 编译 PiWebTray.exe
# 优先使用系统自带 csc（可嵌入 Win32 图标资源）；没有则退回 Add-Type（图标需在托盘内生成，Explorer 显示通用图标）
$ErrorActionPreference = 'Stop'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $dir
try {
    $src = 'PiWebTray.cs'
    $out = 'PiWebTray.exe'
    $ico = 'pi-web-tray.ico'
    $refs = 'System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll'

    if (Test-Path $out) { Remove-Item $out -Force }

    $csc = @(
        "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
        "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1

    if (-not $csc) { throw '未找到 csc.exe（.NET Framework 编译器）' }

    # 缺少 ico 时用旧 exe 现场生成一份
    if (-not (Test-Path $ico)) {
        if (Test-Path $out) {
            & $out --export-icon (Join-Path $dir $ico)
            Remove-Item $out -Force
        }
    }

    $args = @(
        '/nologo', '/target:winexe', '/optimize+', '/platform:anycpu',
        '/codepage:65001', '/utf8output',
        "/out:$out"
    )
    if (Test-Path $ico) { $args += "/win32icon:$ico" }
    $args += "/reference:$($refs -join ',')"
    $args += $src

    & $csc @args
    if ($LASTEXITCODE -ne 0) { throw "csc 编译失败 exit=$LASTEXITCODE" }

    Write-Host ("built -> {0}  ({1} bytes, icon={2})" -f $out, (Get-Item $out).Length, (Test-Path $ico))
}
finally { Pop-Location }