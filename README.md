# PiWebTray

Windows 托盘启动器：静默启动 [pi-web](https://github.com/agegr/pi-web)，只在右下角小图标显示，右键即可更新 / 重启 / 退出。

**23 KB 单文件 exe，零运行时依赖**（纯 WinForms + GDI+，用 Windows 自带的 PowerShell 5.1 `csc` 编译，不需要装 .NET SDK / Node / Python）。

## 效果

- 双击 `PiWebTray.exe`：无控制台窗口、无黑框，右下角出现紫色 "Pi" 托盘图标
- 自动以 `--no-open` 拉起 `pi-web`，**不会自动弹浏览器**（左键点图标或右键菜单才打开）
- 服务日志写在 `%APPDATA%\PiWebTray\pi-web.log`，托盘自身日志写在 `tray.log`

## 菜单

| 操作 | 行为 |
|---|---|
| 左键单击图标 | 用默认浏览器打开 `http://127.0.0.1:30141` |
| 状态 | `运行中 / 启动中 / 运行中(外部) / 已停止 / 更新失败` |
| 打开网页 / 复制地址 | 打开或复制访问地址 |
| **更新 pi-web** | 弹窗确认后停止运行中的实例（外部实例也会先停），执行 `pi-web update`（等价 `npm install -g @agegr/pi-web@latest`），完成后自动重启；失败时气泡里直接给出原因 |
| 重启服务（含外部实例） / 停止服务（含外部实例） | **外部启动的 pi-web 同样生效**：`pi-web stop --port` 优雅关闭 → 若端口未释放则 netstat 定位真正监听的 PID 后 `taskkill /T /F` 兜底（`pi-web stop` 对 Next.js 子进程无效）；重启后由托盘接管 |
| 端口 | 快速切换 30141 / 30180 / 30100 / 8000，或打开配置文件自定义；切换后自动重启 |
| 打开日志目录 | `%APPDATA%\PiWebTray\` |
| 开机自启动 | 在「启动」文件夹写入/删除快捷方式（最小化启动） |
| 退出 | 关闭托盘并停止它拉起的 pi-web 进程 |

其它行为：

- 全局互斥，重复双击不会启动第二份
- 启动时若端口已被 pi-web 占用（例如你自己在控制台跑着），静默识别为「运行中(外部)」，不弹提示、不重复拉起、也不会误杀你的进程；此时更新会提示需手动重启才生效
- 只有端口被**其它程序**占用时才告警，提示你用「端口」菜单换一个
- 服务输出全部重定向到日志，不弹黑框；**每次启动写独立日志** `pi-web-<时间戳>.log`（`cmd >>` 需要独占写句柄，共用一个日志文件会导致后启动的实例瞬间退出），自动只保留最近 5 份
- 后台线程异常、`Application.ThreadException`、未捕获异常都会记录到 `crash.log` 并弹窗，不会再"双击没反应"
- 启动异常写入 `%APPDATA%\PiWebTray\crash.log` 并弹窗提示，不会"双击没反应"

## 前置条件

已全局安装 pi-web：

```bash
npm install -g @agegr/pi-web@latest
```

## 使用

**单文件，零附带**：只要 `PiWebTray.exe` 一个文件，丢到任何目录双击即可（不需要 ini、不需要 ico、不需要安装）。

1. 下载 Releases 里的 `PiWebTray.exe`（或自行编译），放到任意目录，例如 `C:\Tools\PiWebTray\`
2. 双击运行，右下角出现托盘图标
3. 需要开机自启：右键图标勾选「开机自启动」

## 配置

**默认无需任何配置文件**（端口 30141、绑定 127.0.0.1）。需要改端口时，用托盘菜单「端口」切换，会自动在 exe 同目录生成 `pi-web-tray.ini`；也可以手动新建/编辑该文件（修改后点「重启服务」生效）：

```ini
port=30141          # 服务端口
host=127.0.0.1      # 绑定地址，0.0.0.0 = 局域网可访问
args=               # 额外的 pi-web 启动参数
```

## 编译（改了源码才需要）

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
```

用 Windows 自带的 .NET Framework 编译器（`%WINDIR%/Microsoft.NET/Framework64/v4.0.30319/csc.exe`）编译，`/target:winexe` + `/win32icon:pi-web-tray.ico`，产物 `PiWebTray.exe` 约 45 KB（图标同时嵌入 PE，资源管理器里也显示 Pi 图标）。

托盘运行时的图标由 GDI+ 现场生成（16/24/32/48 四档）；`--export-icon` 导出的 `pi-web-tray.ico` 就是同一份数据，用于编译期嵌入。

调试开关：

```powershell
.\PiWebTray.exe --export-icon out.ico   # 导出托盘图标（16/24/32/48 四档）
.\PiWebTray.exe --selftest               # 自检，结果写 %APPDATA%\PiWebTray\selftest.log
```

## 实现要点

- 托盘图标由 GDI+ 程序化生成多分辨率 ICO（16/24/32/48），不依赖外部 ico 文件
- 启动子进程用 `cmd /c call pi-web.cmd --port N --hostname H --no-open >> pi-web.log 2>&1`，`call` 保证 cmd 作为父进程存活，退出时可按进程树精确清理
- 托盘日志与服务日志分文件，避免 node 长时间持有日志句柄导致托盘写入静默失败
- `StopService()` 先调 `pi-web stop --port` 优雅关闭，再回退 `taskkill /T /F`

## License

MIT