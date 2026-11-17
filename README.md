# cmd2ctrl-windows

在 Windows 11 上把 Mac 键盘的 **Command (⌘)** 当作 **Ctrl** 使用：按 `⌘C` 复制、`⌘V` 粘贴、`⌘X` 剪切，习惯与 macOS 一致。

> **仅支持 Windows**（基于 Win32 全局键盘钩子）。Linux 版本见 `cmd2ctrl-linux`（规划中）。

## 特性

- 全局生效，不依赖具体应用
- 映射通过 `config.ini` 配置，支持托盘菜单热重载
- 未配置的组合（`Win+E`、`Win+D`、`Win+Tab` 等）保持系统原生行为
- 单击 `Command` 仍可正常打开开始菜单
- 无需安装 .NET SDK，不依赖额外运行时（使用系统自带 .NET Framework 4.8）
- 托盘图标读取 exe 同目录的 `icon.png`（仓库自带），文件缺失或损坏时自动退回系统默认图标

## 工作原理

1. 低级键盘钩子（`WH_KEYBOARD_LL`）吞掉物理 `Win`（即 Command）的 keydown，使系统不认为 Win 处于按下状态；
2. 命中映射时注入 `Ctrl + 键`，注入序列不带 Win 修饰——因此 VS Code、JetBrains IDE 等**严格匹配修饰键**的应用也能正确识别；
3. 未命中映射时，把 `Win down` 与原始按键转发给系统，保证 `Win+E` 等原生快捷键可用；
4. 单独按下并松开 Command 时补发一次 tap，开始菜单照常弹出。

## 环境要求

- Windows 10 / 11（x64）
- .NET Framework 4.8（Windows 10 1903+ 与 Windows 11 自带）

## 编译

方式一（推荐）：双击 `build.bat`，或执行

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

方式二：手动调用系统自带编译器

```powershell
& "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" `
  /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 `
  /out:".\CmdKeyMapper.exe" `
  /r:System.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll `
  ".\Program.cs"
```

编译前请先从托盘退出正在运行的实例，否则 exe 被占用会编译失败。

## 使用

1. 双击 `CmdKeyMapper.exe`，托盘出现图标即生效；
2. 右键托盘图标可**重新加载配置**、勾选**开机自动启动**或**退出**；
3. 修改 `config.ini` 后无需重启，点“重新加载配置”即可。

> `CmdKeyMapper.exe` 需与 `icon.png`、`config.ini` 放在同一目录；分发时请一并打包，单独拷贝 exe 会使用系统默认托盘图标（功能不受影响）。

### 开机自动启动

勾选托盘菜单中的“开机自动启动”后，程序会把自己写入当前用户的注册表启动项：

```
HKCU\Software\Microsoft\Windows\CurrentVersion\Run
  cmd2ctrl = "<exe 完整路径>"
```

再次点击取消勾选即删除该项（不需要管理员权限）。若把 exe 移动到新目录，菜单会自动显示为未勾选，重新勾选一次即可指向新路径。

## 配置

首次启动会在 exe 同目录生成 `config.ini`（也可参考仓库中的 `config.example.ini`）：

```ini
# 每行一个映射，格式：Cmd+按键=Ctrl+按键
# 删除某行或用 # 注释掉即可停用；未列出的 Win 组合保持系统原样
Cmd+C=Ctrl+C
Cmd+V=Ctrl+V
Cmd+X=Ctrl+X
Cmd+Z=Ctrl+Z
Cmd+A=Ctrl+A
Cmd+S=Ctrl+S
Cmd+F=Ctrl+F
Cmd+T=Ctrl+T
Cmd+W=Ctrl+W
Cmd+N=Ctrl+N
Cmd+O=Ctrl+O
Cmd+P=Ctrl+P
Cmd+Y=Ctrl+Y
```

- 源修饰键可写 `Cmd` 或 `Win`，目标修饰键可写 `Ctrl` 或 `Control`
- 按键支持 `A-Z`、`0-9`
- 物理 `Shift` 会自然叠加，因此 `⌘+Shift+Z` 即 `Ctrl+Shift+Z`，无需单独配置
- 格式错误的行会被忽略，不影响其他映射

## 已知限制

- 对以管理员权限运行的程序无效；需要覆盖这类程序时，本程序也需以管理员身份运行
- `Win+V`（系统剪贴板历史）入口被映射占用
- 无法拦截 `Ctrl+Alt+Del`、`Win+L`、UAC 提权界面等安全桌面输入
- 使用 Raw Input 的游戏可能绕过钩子
- 少数安全软件可能将全局键盘钩子判为可疑行为，建议自行签名后分发

## 相关项目

- `cmd2ctrl-linux`：Linux 下相同目标（规划中，实现方式不同：XKB 布局或 evdev/uinput）

## 诊断

以 `CmdKeyMapper.exe --debug` 启动会在同目录生成 `log.txt`，记录按键事件与映射结果，便于排查问题；正常双击运行不会产生日志。
