using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// "hold-focus": the user's Shift+F1 capture script runs this before it opens the screen clipper.
// The hotkey's Shift reaches the app in front, and a Chromium app (Claude) then draws its focus ring
// round whatever was clicked last; the clipper freezes the screen with that ring in it. An inactive
// window draws no ring, so an invisible window of this process takes the foreground first.
// One line goes to standard output once it is settled: "held" or "refused". The window then stays in
// front until something else (the clipper) takes the foreground, or gives it back after ten seconds, and
// the process ends: when the clipper closes, Windows activates the app under it again.
internal static class FocusHold
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extraInfo);

    // The script tries the clipper twice, three seconds each, then Win+Shift+S.
    private const int HoldMs = 10000;

    // holdMs is shorter only in tests.
    internal static int Run(int holdMs = HoldMs)
    {
        IntPtr previous = GetForegroundWindow();
        using (Form keeper = new Form())
        {
            keeper.ShowInTaskbar = false;
            keeper.FormBorderStyle = FormBorderStyle.None;
            keeper.StartPosition = FormStartPosition.Manual;
            keeper.Location = new System.Drawing.Point(-32000, -32000);
            keeper.Size = new System.Drawing.Size(1, 1);
            keeper.Opacity = 0;
            keeper.Show();
            Application.DoEvents();
            if (!(SetForegroundWindow(keeper.Handle) && GetForegroundWindow() == keeper.Handle))
            {
                // A background process may not take the foreground; an input event of its own allows
                // it. Shift alone types nothing, and the app it reaches is about to go inactive.
                keybd_event(0x10, 0, 0, TypeAhead.OwnKey);
                keybd_event(0x10, 0, 2, TypeAhead.OwnKey);
                SetForegroundWindow(keeper.Handle);
                Application.DoEvents();
            }
            bool held = GetForegroundWindow() == keeper.Handle;
            Console.Out.WriteLine(held ? "held" : "refused");
            Console.Out.Flush();
            if (!held) return 1;

            Stopwatch timer = Stopwatch.StartNew();
            while (GetForegroundWindow() == keeper.Handle)
            {
                if (timer.ElapsedMilliseconds >= holdMs)
                {
                    // The clipper never came: never leave the hidden window in front.
                    if (previous != IntPtr.Zero && IsWindow(previous)) SetForegroundWindow(previous);
                    break;
                }
                Application.DoEvents();
                System.Threading.Thread.Sleep(20);
            }
        }
        return 0;
    }
}
