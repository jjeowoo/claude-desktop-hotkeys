using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

// Alt+` in any app: turns the ` key into the left mouse button where the cursor is, and back.
// While on, a small process holds a low-level keyboard hook: ` down presses the button, ` up
// releases it, so holding the key drags and key repeat sends no extra clicks. Off, nothing runs
// and ` types as usual. Each switch shows a notice at the bottom right.
// The process that stays on holds only the hook and a bare message loop: no WinForms, no UI
// Automation. Each notice is drawn by a short process of its own that closes with it.
internal static class BacktickClick
{
    private const string RunningName = "Local\\ClaudeDesktopHotkeysBacktickClick";
    private const string StopName = "Local\\ClaudeDesktopHotkeysBacktickClickStop";
    private const int Backtick = 0xC0;

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyInfo { internal uint Key, Scan, Flags, Time; internal UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Message { internal IntPtr Window; internal uint Id; internal IntPtr W, L; internal uint Time; internal int X, Y; }
    private delegate IntPtr HookProc(int code, IntPtr kind, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr kind, IntPtr data);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int x, int y, uint data, UIntPtr extraInfo);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern uint MsgWaitForMultipleObjects(uint count, IntPtr[] handles, bool waitAll, uint milliseconds, uint wakeMask);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out Message message, IntPtr window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref Message message);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string name);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

    private static HookProc proc;
    private static IntPtr hook;
    private static bool buttonDown;

    // A second run stops the first; the first run stays as the switched-on state.
    internal static int Toggle()
    {
        bool created;
        using (Mutex running = new Mutex(true, RunningName, out created))
        {
            if (!created)
            {
                try { using (EventWaitHandle stop = EventWaitHandle.OpenExisting(StopName)) stop.Set(); } catch { }
                return 0;
            }
            using (EventWaitHandle stop = new EventWaitHandle(false, EventResetMode.ManualReset, StopName))
                return RunSwitchedOn(stop);
        }
    }

    private static int RunSwitchedOn(EventWaitHandle stop)
    {
        proc = Hook;
        hook = SetWindowsHookEx(13, proc, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero) { ShowNotice("failed"); return 1; }
        ShowNotice("on");
        // What starting up touched is not needed again; hand those pages back to Windows.
        GC.Collect();
        SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
        // The hook runs on this thread, so it pumps messages until the next run signals stop.
        IntPtr[] handles = { stop.SafeWaitHandle.DangerousGetHandle() };
        while (MsgWaitForMultipleObjects(1, handles, false, 0xFFFFFFFF, 0x04FF) == 1)
        {
            Message message;
            while (PeekMessage(out message, IntPtr.Zero, 0, 0, 1))
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        SwitchOff();
        ShowNotice("off");
        return 0;
    }

    private static void ShowNotice(string state)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName, "backtick-notice " + state) { UseShellExecute = false });
        }
        catch { }
    }

    // The notice process: draws one notice, closes after 1.6 s.
    internal static int Notify(string state)
    {
        // System DPI awareness: WinForms scales the notice to the screen it sits on.
        SetProcessDpiAwarenessContext(new IntPtr(-2));
        Application.EnableVisualStyles();
        Application.Run(new Notice(state == "on" ? "Backtick ON" : state == "off" ? "Backtick OFF" : "Backtick 켜기 실패"));
        return 0;
    }

    private static void SwitchOff()
    {
        if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
        // Never leave the button held down.
        if (buttonDown) { buttonDown = false; mouse_event(0x0004, 0, 0, 0, TypeAhead.OwnKey); }
    }

    private static bool IsDown(int key) { return (GetAsyncKeyState(key) & 0x8000) != 0; }

    // Only a bare `: Shift+` is ~, Alt+` is the switch itself, and injected keys are not the person's.
    internal static bool Takes(uint key, bool injected, bool modifierDown, bool up, bool pressed)
    {
        if (key != Backtick || injected) return false;
        return up ? pressed : !modifierDown || pressed;
    }

    private static IntPtr Hook(int code, IntPtr kind, IntPtr data)
    {
        if (code >= 0)
        {
            KeyInfo info = (KeyInfo)Marshal.PtrToStructure(data, typeof(KeyInfo));
            int message = kind.ToInt32();
            bool up = message == 0x101 || message == 0x105;
            bool modifierDown = IsDown(0x10) || IsDown(0x11) || IsDown(0x12) || IsDown(0x5B) || IsDown(0x5C);
            if (Takes(info.Key, (info.Flags & 0x10) != 0, modifierDown, up, buttonDown))
            {
                if (up) { buttonDown = false; mouse_event(0x0004, 0, 0, 0, TypeAhead.OwnKey); }
                else if (!buttonDown) { buttonDown = true; mouse_event(0x0002, 0, 0, 0, TypeAhead.OwnKey); }
                return new IntPtr(1);
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, kind, data);
    }

    internal static void SelfTest()
    {
        if (!Takes(Backtick, false, false, false, false) || Takes(Backtick, false, true, false, false) ||
            Takes(Backtick, true, false, false, false) || Takes(0x41, false, false, false, false) ||
            !Takes(Backtick, false, false, false, true) || !Takes(Backtick, false, true, true, true) ||
            Takes(Backtick, false, false, true, false))
            throw new InvalidOperationException("Backtick click selftest failed.");
    }

    // Bottom right of the screen under the cursor, never taking focus, gone after 1.6 s.
    private sealed class Notice : Form
    {
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ExStyle |= 0x08000000 | 0x80; return cp; }
        }
        internal Notice(string message)
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
            StartPosition = FormStartPosition.Manual; Width = 180; Height = 44;
            Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Left = area.Right - Width - 20; Top = area.Bottom - Height - 20;
            BackColor = Color.FromArgb(35, 35, 40);
            Controls.Add(new Label { Dock = DockStyle.Fill, Text = message, ForeColor = Color.White,
                Font = new Font("Malgun Gothic", 10), Padding = new Padding(12, 0, 12, 0), TextAlign = ContentAlignment.MiddleLeft });
            System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 1600 };
            timer.Tick += delegate { timer.Stop(); Close(); };
            timer.Start(); FormClosed += delegate { timer.Dispose(); };
        }
    }
}
