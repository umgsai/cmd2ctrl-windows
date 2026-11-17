using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

// 把 Mac 键盘的 Command 键（在 Windows 上被识别为 Win 键）映射为 Ctrl：
// 全局低级键盘钩子截获 Win+C / Win+V 等组合，吞掉原事件，再注入 Ctrl+同名键。
internal static class Program
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private const uint LLKHF_INJECTED = 0x10;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private const uint VK_LWIN = 0x5B;
    private const uint VK_RWIN = 0x5C;
    private const uint VK_CONTROL = 0x11;

    // 开机自启动：写入当前用户的 Run 项
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "cmd2ctrl";

    // 映射表从 exe 同目录的 config.ini 加载，修改后可在托盘菜单里重新加载
    private static volatile Dictionary<uint, uint> _cmdMap = new Dictionary<uint, uint>();

    private const string DefaultConfig =
        "# CmdKeyMapper 配置文件\r\n" +
        "# 每行一个映射，格式：Cmd+按键=Ctrl+按键\r\n" +
        "# 删除某行或用 # 注释掉，即可停用该映射；未列出的 Win 组合（Win+E、Win+D 等）保持系统原样\r\n" +
        "# 修改保存后，右键托盘图标选择「重新加载配置」即可生效\r\n" +
        "\r\n" +
        "Cmd+C=Ctrl+C\r\n" +
        "Cmd+V=Ctrl+V\r\n" +
        "Cmd+X=Ctrl+X\r\n" +
        "Cmd+Z=Ctrl+Z\r\n" +
        "Cmd+A=Ctrl+A\r\n" +
        "Cmd+S=Ctrl+S\r\n" +
        "Cmd+F=Ctrl+F\r\n" +
        "Cmd+T=Ctrl+T\r\n" +
        "Cmd+W=Ctrl+W\r\n" +
        "Cmd+N=Ctrl+N\r\n" +
        "Cmd+O=Ctrl+O\r\n" +
        "Cmd+P=Ctrl+P\r\n" +
        "Cmd+Y=Ctrl+Y\r\n";

    private static bool _winDown;        // 物理 Win 是否按下（已被钩子吞掉）
    private static bool _winForwarded;   // 是否已把 Win down 转发给系统
    private static bool _mappedHandled;  // 本次 Win 按下是否已用于映射
    private static volatile bool _paused; // 暂停映射：所有按键原样放行
    private static readonly HashSet<uint> _swallowedKeys = new HashSet<uint>(); // down 被吞的键，up 也吞
    private static readonly HashSet<uint> _forwardedKeys = new HashSet<uint>(); // 已转发给系统的键

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
    private static readonly HookProc _proc = HookCallback; // 静态持有，防止委托被 GC
    private static IntPtr _hook = IntPtr.Zero;

    // 诊断日志：仅在命令行带 --debug 启动时记录
    private static readonly string LogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log.txt");
    private static readonly bool LogOn = Array.IndexOf(Environment.GetCommandLineArgs(), "--debug") > 0;

    private static void Log(string s)
    {
        if (!LogOn) return;
        try
        {
            File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss.fff") + " " + s + Environment.NewLine);
        }
        catch { }
    }

    private static string ForegroundProcess()
    {
        try
        {
            uint pid;
            GetWindowThreadProcessId(GetForegroundWindow(), out pid);
            using (Process p = Process.GetProcessById((int)pid))
            {
                return p.ProcessName;
            }
        }
        catch { return "?"; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    // 托盘图标：使用编译时内嵌的 icon.png 资源；暂停时输出灰度半透明版本
    private static Icon CreateTrayIcon(bool paused)
    {
        try
        {
            using (Bitmap source = LoadEmbeddedIcon())
            {
                if (source != null)
                {
                    Bitmap render = source;
                    try
                    {
                        if (paused)
                        {
                            render = ToGrayscale(source);
                        }

                        IntPtr hIcon = render.GetHicon();
                        try
                        {
                            return (Icon)Icon.FromHandle(hIcon).Clone(); // 复制一份，随后释放原始句柄
                        }
                        finally
                        {
                            DestroyIcon(hIcon);
                        }
                    }
                    finally
                    {
                        if (!ReferenceEquals(render, source))
                        {
                            render.Dispose();
                        }
                    }
                }
            }

            Log("tray icon: 内嵌 icon.png 资源不存在，使用系统默认图标");
        }
        catch (Exception ex)
        {
            Log("tray icon: 加载内嵌图标失败 - " + ex.Message);
        }

        return SystemIcons.Application;
    }

    private static Bitmap LoadEmbeddedIcon()
    {
        using (Stream stream = typeof(Program).Assembly.GetManifestResourceStream("icon.png"))
        {
            if (stream == null)
            {
                return null;
            }

            using (Bitmap raw = new Bitmap(stream))
            {
                return new Bitmap(raw); // 复制一份，避免与资源流绑定
            }
        }
    }

    private static Bitmap ToGrayscale(Bitmap source)
    {
        Bitmap result = new Bitmap(source.Width, source.Height);
        using (Graphics g = Graphics.FromImage(result))
        using (System.Drawing.Imaging.ImageAttributes attributes = new System.Drawing.Imaging.ImageAttributes())
        {
            System.Drawing.Imaging.ColorMatrix matrix = new System.Drawing.Imaging.ColorMatrix(new float[][]
            {
                new float[] { 0.299f, 0.299f, 0.299f, 0f, 0f },
                new float[] { 0.587f, 0.587f, 0.587f, 0f, 0f },
                new float[] { 0.114f, 0.114f, 0.114f, 0f, 0f },
                new float[] { 0f, 0f, 0f, 0.55f, 0f },   // 同时降低不透明度，表达“已暂停”
                new float[] { 0f, 0f, 0f, 0f, 1f },
            });
            attributes.SetColorMatrix(matrix, System.Drawing.Imaging.ColorMatrixFlag.Default,
                System.Drawing.Imaging.ColorAdjustType.Bitmap);
            g.DrawImage(source, new Rectangle(0, 0, source.Width, source.Height),
                0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
        }

        return result;
    }

    private static INPUT MakeKey(uint vk, bool keyUp)
    {
        INPUT input = new INPUT();
        input.type = INPUT_KEYBOARD;
        input.u.ki.wVk = (ushort)vk;
        input.u.ki.dwFlags = keyUp ? KEYEVENTF_KEYUP : 0;
        return input;
    }

    private static void SendKey(uint vk, bool keyUp)
    {
        INPUT[] seq = new INPUT[] { MakeKey(vk, keyUp) };
        SendInput(1, seq, Marshal.SizeOf(typeof(INPUT)));
    }

    private static void SendCtrlCombo(uint vk)
    {
        // 若物理 Shift 处于按下状态，会自然叠加成 Ctrl+Shift+X，无需额外处理
        INPUT[] seq = new INPUT[]
        {
            MakeKey(VK_CONTROL, false),
            MakeKey(vk, false),
            MakeKey(vk, true),
            MakeKey(VK_CONTROL, true),
        };
        SendInput((uint)seq.Length, seq, Marshal.SizeOf(typeof(INPUT)));
        Log("  SendInput Ctrl+0x" + vk.ToString("X2") + " err=" + Marshal.GetLastWin32Error());
    }

    private static void LoadConfig()
    {
        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.ini");
        if (!File.Exists(path))
        {
            try { File.WriteAllText(path, DefaultConfig); } catch { }
        }

        Dictionary<uint, uint> map = new Dictionary<uint, uint>();
        if (File.Exists(path))
        {
            string[] lines = new string[0];
            try { lines = File.ReadAllLines(path); } catch { }

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";"))
                {
                    continue;
                }

                int eq = line.IndexOf('=');
                if (eq <= 0)
                {
                    Log("config: 忽略第 " + (i + 1) + " 行（缺少 =）: " + line);
                    continue;
                }

                uint source;
                uint target;
                if (!TryParseCombo(line.Substring(0, eq).Trim(), true, out source)
                    || !TryParseCombo(line.Substring(eq + 1).Trim(), false, out target))
                {
                    Log("config: 忽略第 " + (i + 1) + " 行（格式应为 Cmd+X=Ctrl+X）: " + line);
                    continue;
                }

                map[source] = target;
            }
        }

        _cmdMap = map;
        Log("config: 已加载 " + map.Count + " 条映射，路径 " + path);
    }

    private static bool TryParseCombo(string text, bool expectWin, out uint vk)
    {
        vk = 0;
        string[] parts = text.Split('+');
        if (parts.Length != 2)
        {
            return false;
        }

        string mod = parts[0].Trim().ToLowerInvariant();
        string key = parts[1].Trim();
        if (key.Length != 1)
        {
            return false;
        }

        if (expectWin)
        {
            if (mod != "cmd" && mod != "win")
            {
                return false;
            }
        }
        else
        {
            if (mod != "ctrl" && mod != "control")
            {
                return false;
            }
        }

        char c = char.ToUpperInvariant(key[0]);
        if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
        {
            vk = c;
            return true;
        }
        return false;
    }

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
        {
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        int msg = wParam.ToInt32();
        KBDLLHOOKSTRUCT k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
        bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
        bool up = msg == WM_KEYUP || msg == WM_SYSKEYUP;

        Log(string.Format("evt vk=0x{0:X2} {1} inj={2} winDown={3} fg={4}",
            k.vkCode, down ? "DOWN" : "UP", (k.flags & LLKHF_INJECTED) != 0, _winDown, ForegroundProcess()));

        // 注入事件（含本程序自己发的）直接放行，避免重复处理
        if ((k.flags & LLKHF_INJECTED) != 0)
        {
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        // 暂停映射：所有按键原样放行，Win 完全交还系统
        if (_paused)
        {
            _winDown = false;
            _winForwarded = false;
            _mappedHandled = false;
            _swallowedKeys.Clear();
            _forwardedKeys.Clear();
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        if (k.vkCode == VK_LWIN || k.vkCode == VK_RWIN)
        {
            if (down)
            {
                if (!_winDown)
                {
                    _winDown = true;
                    _winForwarded = false;
                    _mappedHandled = false;
                }
                // 先吞掉 Win down，等后续按键判断是映射组合还是系统组合
                return (IntPtr)1;
            }
            if (up)
            {
                _winDown = false;
                if (_winForwarded)
                {
                    _winForwarded = false;
                    SendKey(VK_LWIN, true);   // 释放之前转发给系统的 Win
                }
                else if (!_mappedHandled)
                {
                    SendKey(VK_LWIN, false);  // 单击 Win：补发 tap，正常打开开始菜单
                    SendKey(VK_LWIN, true);
                }
                _mappedHandled = false;
                return (IntPtr)1;
            }
        }

        if (down)
        {
            if (_winDown)
            {
                if (_cmdMap.ContainsKey(k.vkCode))
                {
                    // 系统从未收到 Win down，注入的 Ctrl+X 不带 Win 修饰
                    _mappedHandled = true;
                    _swallowedKeys.Add(k.vkCode);
                    SendCtrlCombo(_cmdMap[k.vkCode]);
                    return (IntPtr)1;
                }

                // 非映射组合：把 Win down 和这个键转发给系统，保持 Win+E 等原生行为
                if (!_winForwarded)
                {
                    _winForwarded = true;
                    _mappedHandled = true;
                    SendKey(VK_LWIN, false);
                }
                _forwardedKeys.Add(k.vkCode);
                SendKey(k.vkCode, false);
                return (IntPtr)1;
            }
        }
        else if (up)
        {
            if (_swallowedKeys.Remove(k.vkCode))
            {
                return (IntPtr)1;
            }
            if (_forwardedKeys.Remove(k.vkCode))
            {
                SendKey(k.vkCode, true);
                return (IntPtr)1;
            }
        }

        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    [STAThread]
    private static void Main()
    {
        bool createdNew;
        using (Mutex mutex = new Mutex(true, "Global\\MacCmdToCtrl", out createdNew))
        {
            if (!createdNew)
            {
                return; // 单实例：已在运行
            }

            LoadConfig();

            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero)
            {
                Log("hook failed err=" + Marshal.GetLastWin32Error());
                MessageBox.Show("安装键盘钩子失败，错误码 " + Marshal.GetLastWin32Error(), "CmdKeyMapper");
                return;
            }
            Log("start hook=" + _hook + " log=" + LogPath);

            Icon trayIcon = CreateTrayIcon(false);
            NotifyIcon tray = new NotifyIcon();
            tray.Icon = trayIcon;
            tray.Text = TrayText(false);
            tray.Visible = true;

            ContextMenuStrip menu = new ContextMenuStrip();

            ToolStripMenuItem enableItem = new ToolStripMenuItem("启用映射");
            enableItem.CheckOnClick = true;
            enableItem.Checked = true;
            enableItem.CheckedChanged += new EventHandler(delegate { OnToggleMapping(enableItem, tray); });
            menu.Items.Add(enableItem);

            menu.Items.Add("重新加载配置", null, new EventHandler(OnReloadConfig));

            ToolStripMenuItem autoStart = new ToolStripMenuItem("开机自动启动");
            autoStart.CheckOnClick = true;
            autoStart.Checked = IsAutoStartEnabled();
            autoStart.Click += new EventHandler(OnToggleAutoStart);
            menu.Items.Add(autoStart);

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, new EventHandler(OnExit));

            tray.ContextMenuStrip = menu;

            Application.EnableVisualStyles();
            Application.Run();

            tray.Visible = false;
            tray.Dispose();
            trayIcon.Dispose();
            menu.Dispose();
        }
    }

    private static void OnReloadConfig(object sender, EventArgs e)
    {
        LoadConfig();
    }

    private static void OnToggleMapping(ToolStripMenuItem item, NotifyIcon tray)
    {
        _paused = !item.Checked;

        // 切换时清空按键状态，避免暂停/恢复后出现“Win 卡住”
        _winDown = false;
        _winForwarded = false;
        _mappedHandled = false;
        _swallowedKeys.Clear();
        _forwardedKeys.Clear();

        Icon old = tray.Icon;
        tray.Icon = CreateTrayIcon(_paused);
        if (old != null && !ReferenceEquals(old, SystemIcons.Application))
        {
            old.Dispose();
        }
        tray.Text = TrayText(_paused);

        Log("mapping paused=" + _paused);
    }

    private static string TrayText(bool paused)
    {
        return paused ? "Cmd 映射已暂停（Win 保持系统行为）" : "Cmd(Win) 已映射为 Ctrl";
    }

    private static string AutoStartCommand()
    {
        return "\"" + Application.ExecutablePath + "\"";
    }

    private static bool IsAutoStartEnabled()
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath))
            {
                if (key == null)
                {
                    return false;
                }
                string value = key.GetValue(RunValueName) as string;
                // 路径不一致说明是旧目录留下的残留项，视为未启用
                return value != null
                    && string.Equals(value.Trim(), AutoStartCommand(), StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            return false;
        }
    }

    private static void SetAutoStart(bool enabled)
    {
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
        {
            if (key == null)
            {
                throw new InvalidOperationException("无法打开注册表自启动项");
            }

            if (enabled)
            {
                key.SetValue(RunValueName, AutoStartCommand(), RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(RunValueName, false);
            }
        }
    }

    private static void OnToggleAutoStart(object sender, EventArgs e)
    {
        ToolStripMenuItem item = (ToolStripMenuItem)sender;
        try
        {
            SetAutoStart(item.Checked);
        }
        catch (Exception ex)
        {
            item.Checked = !item.Checked;
            MessageBox.Show("设置开机自动启动失败：" + ex.Message, "CmdKeyMapper");
        }
    }

    private static void OnExit(object sender, EventArgs e)
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
        Application.Exit();
    }

}
