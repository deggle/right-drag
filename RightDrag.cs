// RightDrag - drag windows by their title bar with the right mouse button,
// without activating them or changing their z-order.
//
// Build: C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /optimize /out:RightDrag.exe RightDrag.cs

using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using System.Reflection;
using Microsoft.Win32;

[assembly: AssemblyTitle("RightDrag")]
[assembly: AssemblyProduct("RightDrag")]
[assembly: AssemblyDescription("Drag windows by their title bar with the right mouse button, without bringing them to the front.")]
[assembly: AssemblyCompany("Tim Alston")]
[assembly: AssemblyCopyright("Copyright © 2026 Tim Alston")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

static class RightDrag
{
    // ---- Win32 ----
    const int WH_MOUSE_LL = 14;
    const int WM_MOUSEMOVE = 0x0200, WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;
    const int WM_NCHITTEST = 0x0084;
    const int HTCAPTION = 2, HTTRANSPARENT = -1;
    const uint GA_PARENT = 1, GA_ROOT = 2;
    const uint SMTO_ABORTIFHUNG = 0x0002;
    const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010,
               SWP_NOOWNERZORDER = 0x0200, SWP_ASYNCWINDOWPOS = 0x4000;
    const int SW_SHOWNOACTIVATE = 4;
    const int SM_CXDRAG = 68, SM_CYDRAG = 69;
    const uint INPUT_MOUSE = 0, MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010;
    static readonly IntPtr ReplayMarker = new IntPtr(0x52445247); // "RDRG"

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    struct WINDOWPLACEMENT { public int length, flags, showCmd; public POINT ptMin, ptMax; public RECT rcNormal; }
    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public MOUSEINPUT mi; }

    delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT pt);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] static extern bool PhysicalToLogicalPointForPerMonitorDPI(IntPtr hwnd, ref POINT pt);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT wp);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);

    // ---- state ----
    static IntPtr hook;
    static HookProc hookProc = HookCallback; // keep delegate alive
    static bool enabled = true;

    static bool pressed;      // right button went down on a caption, we swallowed it
    static bool dragging;     // moved past the drag threshold
    static IntPtr target;
    static POINT pressPt;     // cursor at press (or at un-maximise)
    static RECT startRect;    // window rect at press (or at un-maximise)
    static bool wasMaximised;

    // Pending move, handed from the hook to the mover thread. Only the latest one matters:
    // stale positions are overwritten rather than queued, so the window never lags behind.
    static readonly object moveLock = new object();
    static readonly AutoResetEvent moveSignal = new AutoResetEvent(false);
    static IntPtr moveHwnd;
    static int moveX, moveY;
    static bool movePending;
    static bool restorePending;
    static int restoreW, restoreH;

    [STAThread]
    static void Main()
    {
        bool created;
        using (new Mutex(true, "RightDrag_SingleInstance", out created))
        {
            if (!created) return;

            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { } // per-monitor v2: physical pixels everywhere

            Thread mover = new Thread(MoverLoop);
            mover.IsBackground = true;
            mover.Start();

            Application.EnableVisualStyles();
            hook = SetWindowsHookEx(WH_MOUSE_LL, hookProc, GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero)
            {
                MessageBox.Show("Could not install mouse hook.", "RightDrag");
                return;
            }

            NotifyIcon tray = BuildTray();
            Application.Run();
            tray.Visible = false;
            UnhookWindowsHookEx(hook);
        }
    }

    // ---- hook ----
    static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || !enabled) return CallNextHookEx(hook, nCode, wParam, lParam);

        MSLLHOOKSTRUCT m = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
        if (m.dwExtraInfo == ReplayMarker) return CallNextHookEx(hook, nCode, wParam, lParam);

        int msg = wParam.ToInt32();

        if (msg == WM_RBUTTONDOWN && !pressed)
        {
            IntPtr root = CaptionWindowAt(m.pt);
            if (root != IntPtr.Zero)
            {
                pressed = true;
                dragging = false;
                target = root;
                pressPt = m.pt;
                GetWindowRect(root, out startRect);
                wasMaximised = IsZoomed(root);
                return new IntPtr(1); // swallow: the window never sees the click, so it isn't activated
            }
        }
        else if (msg == WM_MOUSEMOVE && pressed)
        {
            if (!dragging &&
                (Math.Abs(m.pt.X - pressPt.X) >= GetSystemMetrics(SM_CXDRAG) ||
                 Math.Abs(m.pt.Y - pressPt.Y) >= GetSystemMetrics(SM_CYDRAG)))
            {
                dragging = true;
                if (wasMaximised) Unmaximise(m.pt);
            }
            if (dragging)
                RequestMove(startRect.Left + (m.pt.X - pressPt.X), startRect.Top + (m.pt.Y - pressPt.Y));
        }
        else if (msg == WM_RBUTTONUP && pressed)
        {
            bool wasDrag = dragging;
            pressed = dragging = false;
            target = IntPtr.Zero;
            // A plain right-click (no drag) is replayed so the title-bar context menu still works.
            if (!wasDrag) ThreadPool.QueueUserWorkItem(delegate { ReplayRightClick(); });
            return new IntPtr(1);
        }

        return CallNextHookEx(hook, nCode, wParam, lParam);
    }

    // Returns the top-level window if the point is on its title bar / drag area, else Zero.
    // Mirrors Windows' own dispatch: ask the deepest window, walking up while it answers HTTRANSPARENT.
    static IntPtr CaptionWindowAt(POINT pt)
    {
        IntPtr w = WindowFromPoint(pt);
        if (w == IntPtr.Zero) return IntPtr.Zero;
        IntPtr root = GetAncestor(w, GA_ROOT);

        for (int depth = 0; w != IntPtr.Zero && depth < 32; depth++)
        {
            POINT local = pt;
            try { PhysicalToLogicalPointForPerMonitorDPI(w, ref local); } catch { }
            IntPtr lp = new IntPtr((local.Y << 16) | (local.X & 0xFFFF));
            IntPtr res;
            if (SendMessageTimeout(w, WM_NCHITTEST, IntPtr.Zero, lp, SMTO_ABORTIFHUNG, 100, out res) == IntPtr.Zero)
                return IntPtr.Zero; // hung or timed out
            int ht = (int)res.ToInt64();
            if (ht == HTCAPTION) return root;
            if (ht != HTTRANSPARENT) return IntPtr.Zero;
            if (w == root) return IntPtr.Zero;
            w = GetAncestor(w, GA_PARENT);
        }
        return IntPtr.Zero;
    }

    // Like the normal drag: restore to normal size, keeping the cursor at the same relative spot on the title bar.
    static void Unmaximise(POINT cur)
    {
        WINDOWPLACEMENT wp = new WINDOWPLACEMENT();
        wp.length = Marshal.SizeOf(typeof(WINDOWPLACEMENT));
        if (!GetWindowPlacement(target, ref wp)) return;

        int w = wp.rcNormal.Right - wp.rcNormal.Left;
        int h = wp.rcNormal.Bottom - wp.rcNormal.Top;
        int maxW = Math.Max(1, startRect.Right - startRect.Left);
        double ratio = (double)(pressPt.X - startRect.Left) / maxW;

        int left = cur.X - (int)(ratio * w);
        int top = startRect.Top;

        lock (moveLock)
        {
            restorePending = true;
            restoreW = w; restoreH = h;
        }

        startRect.Left = left; startRect.Top = top;
        startRect.Right = left + w; startRect.Bottom = top + h;
        pressPt = cur;
    }

    static void RequestMove(int x, int y)
    {
        lock (moveLock)
        {
            moveHwnd = target;
            moveX = x; moveY = y;
            movePending = true;
        }
        moveSignal.Set();
    }

    // Applies moves synchronously, one at a time. While a slow window is still processing a move,
    // newer cursor positions simply replace the pending one, so it always jumps to where the mouse is now.
    static void MoverLoop()
    {
        while (true)
        {
            moveSignal.WaitOne();
            IntPtr hwnd; int x, y, w = 0, h = 0; bool restore;
            lock (moveLock)
            {
                if (!movePending) continue;
                hwnd = moveHwnd; x = moveX; y = moveY;
                restore = restorePending;
                if (restore) { w = restoreW; h = restoreH; }
                movePending = restorePending = false;
            }

            const uint flags = SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER;
            if (restore)
            {
                ShowWindow(hwnd, SW_SHOWNOACTIVATE);
                SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, flags);
            }
            else
            {
                SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, flags | SWP_NOSIZE);
            }
        }
    }

    static void ReplayRightClick()
    {
        INPUT[] inputs = new INPUT[2];
        inputs[0].type = inputs[1].type = INPUT_MOUSE;
        inputs[0].mi.dwFlags = MOUSEEVENTF_RIGHTDOWN;
        inputs[1].mi.dwFlags = MOUSEEVENTF_RIGHTUP;
        inputs[0].mi.dwExtraInfo = inputs[1].mi.dwExtraInfo = ReplayMarker;
        SendInput(2, inputs, Marshal.SizeOf(typeof(INPUT)));
    }

    // ---- tray ----
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "RightDrag";

    static NotifyIcon BuildTray()
    {
        NotifyIcon tray = new NotifyIcon();
        tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        tray.Text = "RightDrag - right-drag title bars without focusing";

        ContextMenuStrip menu = new ContextMenuStrip();

        ToolStripMenuItem enabledItem = new ToolStripMenuItem("Enabled");
        enabledItem.Checked = true;
        enabledItem.Click += delegate
        {
            enabled = !enabled;
            pressed = dragging = false;
            enabledItem.Checked = enabled;
        };

        ToolStripMenuItem startupItem = new ToolStripMenuItem("Start with Windows");
        startupItem.Checked = IsStartupEnabled();
        startupItem.Click += delegate
        {
            SetStartup(!startupItem.Checked);
            startupItem.Checked = IsStartupEnabled();
        };

        ToolStripMenuItem aboutItem = new ToolStripMenuItem("About RightDrag...");
        aboutItem.Click += delegate { ShowAbout(tray.Icon); };

        ToolStripMenuItem exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += delegate { Application.Exit(); };

        menu.Items.Add(enabledItem);
        menu.Items.Add(startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(aboutItem);
        menu.Items.Add(exitItem);

        tray.ContextMenuStrip = menu;
        tray.DoubleClick += delegate { enabledItem.PerformClick(); };
        tray.Visible = true;
        return tray;
    }

    // ---- about ----
    static Form aboutForm;

    static T Attr<T>() where T : Attribute
    {
        return (T)Attribute.GetCustomAttribute(Assembly.GetExecutingAssembly(), typeof(T));
    }

    static void ShowAbout(Icon icon)
    {
        if (aboutForm != null) { aboutForm.Activate(); return; }

        Version v = Assembly.GetExecutingAssembly().GetName().Version;

        Form f = new Form();
        f.Text = "About RightDrag";
        f.Icon = icon;
        f.FormBorderStyle = FormBorderStyle.FixedDialog;
        f.MaximizeBox = f.MinimizeBox = false;
        f.ShowInTaskbar = false;
        f.StartPosition = FormStartPosition.CenterScreen;
        f.AutoSize = true;
        f.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        f.Padding = new Padding(16);

        int iconSize = 48 * f.DeviceDpi / 96;
        PictureBox pic = new PictureBox();
        pic.Image = new Icon(icon, iconSize, iconSize).ToBitmap();
        pic.SizeMode = PictureBoxSizeMode.Zoom;
        pic.Size = new Size(iconSize, iconSize);
        pic.Margin = new Padding(0, 0, 16, 0);

        FlowLayoutPanel text = new FlowLayoutPanel();
        text.FlowDirection = FlowDirection.TopDown;
        text.AutoSize = true;
        text.WrapContents = false;

        Label title = new Label();
        title.Text = "RightDrag";
        title.Font = new Font(f.Font.FontFamily, f.Font.Size * 1.6f, FontStyle.Bold);
        title.AutoSize = true;
        text.Controls.Add(title);

        string[] lines = {
            "Version " + v.Major + "." + v.Minor + "." + v.Build,
            "",
            Attr<AssemblyDescriptionAttribute>().Description,
            "Inspired by the Adjust-drag of Acorn RISC OS.",
            "",
            "Written by Tim Alston and Claude (Anthropic).",
            Attr<AssemblyCopyrightAttribute>().Copyright + ". Released under the MIT License."
        };
        foreach (string line in lines)
        {
            Label l = new Label();
            l.Text = line;
            l.AutoSize = true;
            l.MaximumSize = new Size(360 * f.DeviceDpi / 96, 0);
            text.Controls.Add(l);
        }

        Button ok = new Button();
        ok.Text = "OK";
        ok.Click += delegate { f.Close(); }; // modeless, so DialogResult alone wouldn't close it
        ok.AutoSize = true;
        ok.Anchor = AnchorStyles.Right;
        ok.Margin = new Padding(0, 16, 0, 0);
        f.AcceptButton = f.CancelButton = ok;

        TableLayoutPanel layout = new TableLayoutPanel();
        layout.AutoSize = true;
        layout.ColumnCount = 2;
        layout.RowCount = 2;
        layout.Controls.Add(pic, 0, 0);
        layout.Controls.Add(text, 1, 0);
        layout.Controls.Add(ok, 1, 1);
        f.Controls.Add(layout);

        aboutForm = f;
        f.FormClosed += delegate { aboutForm = null; f.Dispose(); };
        f.Show();
        f.Activate();
    }

    static bool IsStartupEnabled()
    {
        using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
            return k != null && k.GetValue(RunName) != null;
    }

    static void SetStartup(bool on)
    {
        using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (on) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
            else k.DeleteValue(RunName, false);
        }
    }
}
