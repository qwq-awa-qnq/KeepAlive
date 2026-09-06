# Keep-Alive 保活看门狗

Windows 进程保活工具（WinForms / .NET Framework 4.x，Win7 SP1+ 可用）。
目标进程退出后自动拉起；防重启风暴；日志留痕；可按监控项选择「隐藏到托盘 / 最小化 / 开机自启自动检测」；托盘兜底图标可唤回被隐藏窗口；可结束目标进程。

## 目录

| 目录 | 说明 |
|---|---|
| `KeepAlive-DeepSeek/` | 鲸鱼图标版（任务栏/托盘用 DeepSeek 官网鲸鱼） |
| `KeepAlive-盾牌/` | 盾牌图标版 |
| `keep-alive.bat` | 无 GUI 纯命令行版（Win7+ 零依赖） |

每个版本文件夹内：`KeepAlive.exe`（编译产物）、`KeepAlive.cs`（源码，csc 编译）、`icon.ico`（内嵌图标）、`keep-alive.cfg`（运行后自动生成）。

## 环境要求

- Windows 7 SP1 / 8 / 10 / 11（32/64 位均可）
- .NET Framework 4.x（Win10/11 自带；Win7 需安装）
- 无需管理员（保活管理员进程时请以管理员运行本工具）
- 无其它运行时依赖（命令行匹配走系统 WMI）

## 快速使用

1. 双击 `KeepAlive.exe`（开机静默自启时主窗口自动隐藏进托盘）
2. `+ 添加监控`：填目标程序路径（进程名自动带出），需要时可填启动参数/工作目录/命令行特征
3. 列表前几列勾选框：`隐藏到托盘` / `最小化` / `开机检测`（开机自启后自动检测）
4. `▶ 启动全部` 或双击该行启停
5. 目标无托盘图标时，被隐藏软件会在系统托盘生成一个兜底图标（点它可唤回窗口）

## 源码编译（无 VS 也可）

```powershell
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$refs = "/r:System.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll /r:System.Core.dll /r:Microsoft.VisualBasic.dll"
& $csc /nologo /target:winexe /codepage:65001 /win32icon:icon.ico /resource:icon.ico /out:KeepAlive.exe $refs KeepAlive.cs
```

## 说明

- 隐藏/最小化对无视启动参数的程序采用“启动后持续补刀 + 托盘兜底唤回”策略
- 结束进程、删除日志（移到回收站）等操作均在界面内提供
- 配置文件与日志生成于 exe 同目录（`keep-alive.cfg` / `keep-alive.log`）
