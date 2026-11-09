using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

// "pin-image [--serve] [--at X,Y] [file]": the user's own Shift+F1 capture hotkey hands its capture
// here in place of StrokesPlus's sp.DisplayImage. The window behaves the same (always on top, drag
// to move, wheel to zoom, double-click or Esc to close), and its right-click menu adds translation
// (SelectionTranslator) to the Save As that right-click used to open. It opens over the captured
// area itself, found from the point where the capture ended (--at).
// With --serve the process stays after its windows close and takes later captures by message
// (PinMessage, sent by Shift+F1 through sp.SendMessage), so they appear without a process start.
internal static class PinnedImage
{
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string title);

    internal const string WindowTitle = "claude-desktop-hotkeys 캡처";
    // ASCII, for StrokesPlus's sp.FindWindow.
    internal const string ServerTitle = "claude-desktop-hotkeys-pin-server";
    // WM_APP + 1; wParam and lParam are the point where the capture ended.
    internal const int PinMessage = 0x8001;
    private static readonly ApplicationContext context = new ApplicationContext();
    private static int openForms;
    private static PinServer server;

    internal static int Run(string[] args)
    {
        bool serve;
        Point? at;
        string path;
        if (!ParseArguments(args, out serve, out at, out path)) return 2;
        // Per-monitor physical pixels, as StrokesPlus uses: its cursor point and the screen pixels
        // mean the same here on every monitor, and the capture is shown one to one.
        SetProcessDpiAwarenessContext(new IntPtr(-4));
        Application.EnableVisualStyles();
        bool shown = Pin(at ?? Cursor.Position, path);
        // One server: a second would never be found, and would only hold memory.
        if (serve && FindWindow(null, ServerTitle) == IntPtr.Zero) server = new PinServer();
        if (!shown && server == null) return 1;
        Application.Run(context);
        return 0;
    }

    internal static bool ParseArguments(string[] args, out bool serve, out Point? at, out string path)
    {
        serve = false;
        at = null;
        path = null;
        int index = 1;
        if (args.Length > index && args[index] == "--serve") { serve = true; index++; }
        if (args.Length > index + 1 && args[index] == "--at")
        {
            string[] parts = args[index + 1].Split(',');
            int x, y;
            if (parts.Length != 2 || !int.TryParse(parts[0], out x) || !int.TryParse(parts[1], out y)) return false;
            at = new Point(x, y);
            index += 2;
        }
        if (args.Length > index) path = args[index++];
        return args.Length == index && args.Length > 0 && args[0] == "pin-image";
    }

    // From the clipboard, where the capture tool left it, or from a file for testing.
    private static bool Pin(Point corner, string path)
    {
        Bitmap image = path != null ? FromFile(path) : FromClipboard();
        if (image == null)
        {
            ReasoningSwitch.AppendHistory(Environment.NewLine + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                " Pin image: no image " + (path != null ? "in the file" : "on the clipboard") + Environment.NewLine);
            return false;
        }
        Track(new PinWindow(image, Place(image, corner))).Show();
        return true;
    }

    // The process lives while any of its windows does, so closing the capture leaves its translation
    // up, and a server lives on after them.
    internal static Form Track(Form form)
    {
        Interlocked.Increment(ref openForms);
        form.FormClosed += delegate { Interlocked.Decrement(ref openForms); ExitIfIdle(); };
        return form;
    }

    private static void ExitIfIdle()
    {
        if (openForms == 0 && (server == null || server.Retired)) context.ExitThread();
    }

    private static Bitmap FromFile(string path)
    {
        try { using (Image loaded = Image.FromFile(path)) return new Bitmap(loaded); }
        catch { return null; }
    }

    // The capture tool may still hold the clipboard for a moment.
    private static Bitmap FromClipboard()
    {
        for (int tries = 0; tries < 20; tries++)
        {
            try
            {
                if (Clipboard.ContainsImage())
                    using (Image copied = Clipboard.GetImage())
                        if (copied != null) return new Bitmap(copied);
            }
            catch (ExternalException) { }
            Thread.Sleep(50);
        }
        return null;
    }

    // The screen area the capture was taken from. The capture ended at one of its corners, so the
    // screen around that point is compared with the image at each of the four ways it can lie.
    internal static Rectangle Place(Bitmap image, Point corner)
    {
        Stopwatch timer = Stopwatch.StartNew();
        // The capture tool's dimmed overlay may still be fading out; look again for a moment.
        while (true)
        {
            Rectangle found;
            if (FindOnScreen(image, corner, out found)) return found;
            if (timer.ElapsedMilliseconds >= 400) break;
            Thread.Sleep(40);
        }
        return Fallback(image.Size, corner, Screen.FromPoint(corner).WorkingArea);
    }

    // The screen has changed since: as if drawn down and to the right, the usual way, kept on screen.
    internal static Rectangle Fallback(Size size, Point corner, Rectangle area)
    {
        int x = Math.Max(area.Left, Math.Min(corner.X - size.Width, area.Right - size.Width));
        int y = Math.Max(area.Top, Math.Min(corner.Y - size.Height, area.Bottom - size.Height));
        return new Rectangle(new Point(x, y), size);
    }

    // Down-right first: on a plain image that matches every way, the usual way wins.
    internal static int[] Origins(int corner, int size)
    {
        return new[] { corner - size, corner - size + 1, corner - size - 1, corner, corner + 1, corner - 1 };
    }

    private static bool FindOnScreen(Bitmap image, Point corner, out Rectangle found)
    {
        found = Rectangle.Empty;
        Rectangle area = Rectangle.Intersect(Rectangle.FromLTRB(corner.X - image.Width - 1, corner.Y - image.Height - 1,
            corner.X + image.Width + 2, corner.Y + image.Height + 2), SystemInformation.VirtualScreen);
        if (area.Width < image.Width || area.Height < image.Height) return false;
        try
        {
            using (Bitmap shot = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb))
            {
                using (Graphics graphics = Graphics.FromImage(shot)) graphics.CopyFromScreen(area.Location, Point.Empty, area.Size);
                return Match(image, shot, area.Location, Origins(corner.X, image.Width), Origins(corner.Y, image.Height), out found);
            }
        }
        catch (Win32Exception) { return false; }
    }

    // A grid of up to 12 by 12 pixels; one in twenty may differ (a blinking caret, a tooltip).
    internal static bool Match(Bitmap image, Bitmap shot, Point shotOrigin, int[] lefts, int[] tops, out Rectangle found)
    {
        found = Rectangle.Empty;
        List<Point> samples = new List<Point>();
        int columns = Math.Min(12, image.Width), rows = Math.Min(12, image.Height);
        for (int row = 0; row < rows; row++)
            for (int column = 0; column < columns; column++)
                samples.Add(new Point((2 * column + 1) * image.Width / (2 * columns), (2 * row + 1) * image.Height / (2 * rows)));
        Color[] expected = new Color[samples.Count];
        for (int i = 0; i < samples.Count; i++) expected[i] = image.GetPixel(samples[i].X, samples[i].Y);
        int best = samples.Count / 20 + 1;
        foreach (int top in tops)
            foreach (int left in lefts)
            {
                int x = left - shotOrigin.X, y = top - shotOrigin.Y, misses = 0;
                if (x < 0 || y < 0 || x + image.Width > shot.Width || y + image.Height > shot.Height) continue;
                for (int i = 0; i < samples.Count && misses < best; i++)
                {
                    Color want = expected[i], seen = shot.GetPixel(x + samples[i].X, y + samples[i].Y);
                    if (Math.Abs(want.R - seen.R) + Math.Abs(want.G - seen.G) + Math.Abs(want.B - seen.B) > 30) misses++;
                }
                if (misses < best) { best = misses; found = new Rectangle(left, top, image.Width, image.Height); }
            }
        return found != Rectangle.Empty;
    }

    internal static Size Zoomed(Size image, double zoom)
    {
        return new Size(Math.Max(1, (int)Math.Round(image.Width * zoom)), Math.Max(1, (int)Math.Round(image.Height * zoom)));
    }

    // Keeps the point under the cursor in place, as StrokesPlus's zoom did.
    internal static Point ZoomOrigin(Point cursor, Point location, Size before, Size after)
    {
        double x = (cursor.X - location.X) / (double)before.Width, y = (cursor.Y - location.Y) / (double)before.Height;
        return new Point(cursor.X - (int)Math.Round(x * after.Width), cursor.Y - (int)Math.Round(y * after.Height));
    }

    private static DateTime BuildTime()
    {
        try { return File.GetLastWriteTimeUtc(Application.ExecutablePath); }
        catch { return DateTime.MinValue; }
    }

    internal static void SelfTest()
    {
        Rectangle area = new Rectangle(0, 0, 1920, 1040);
        if (Zoomed(new Size(400, 100), 1.12) != new Size(448, 112) || Zoomed(new Size(3, 3), 0.01) != new Size(1, 1) ||
            ZoomOrigin(new Point(150, 150), new Point(100, 100), new Size(200, 100), new Size(400, 200)) != new Point(50, 50) ||
            Fallback(new Size(200, 100), new Point(500, 500), area) != new Rectangle(300, 400, 200, 100) ||
            Fallback(new Size(200, 100), new Point(50, 1035), area) != new Rectangle(0, 935, 200, 100) ||
            Origins(100, 30)[0] != 70 || Origins(100, 30)[3] != 100)
            throw new InvalidOperationException("Pinned image geometry selftest failed.");
        bool serve;
        Point? at;
        string path;
        if (!ParseArguments(new[] { "pin-image" }, out serve, out at, out path) || serve || at != null || path != null ||
            !ParseArguments(new[] { "pin-image", "--serve", "--at", "2700,-300" }, out serve, out at, out path) || !serve || at != new Point(2700, -300) || path != null ||
            !ParseArguments(new[] { "pin-image", "--at", "5,6", "C:\\a.png" }, out serve, out at, out path) || serve || at != new Point(5, 6) || path != "C:\\a.png" ||
            !ParseArguments(new[] { "pin-image", "C:\\a.png" }, out serve, out at, out path) || at != null || path != "C:\\a.png" ||
            ParseArguments(new[] { "pin-image", "--at", "5" }, out serve, out at, out path) ||
            ParseArguments(new[] { "pin-image", "a", "b" }, out serve, out at, out path))
            throw new InvalidOperationException("Pinned image argument selftest failed.");
        // A noisy screen with the capture cut from it: found whichever corner the capture ended at.
        Random random = new Random(7);
        using (Bitmap shot = new Bitmap(300, 200, PixelFormat.Format32bppArgb))
        using (Bitmap other = new Bitmap(100, 60, PixelFormat.Format32bppArgb))
        {
            for (int y = 0; y < shot.Height; y++)
                for (int x = 0; x < shot.Width; x++) shot.SetPixel(x, y, Color.FromArgb(random.Next(256), random.Next(256), random.Next(256)));
            for (int y = 0; y < other.Height; y++)
                for (int x = 0; x < other.Width; x++) other.SetPixel(x, y, Color.FromArgb(random.Next(256), random.Next(256), random.Next(256)));
            using (Bitmap capture = shot.Clone(new Rectangle(120, 70, 100, 60), PixelFormat.Format32bppArgb))
            {
                Rectangle found, expected = new Rectangle(120, 70, 100, 60);
                Point origin = new Point(1000, 500);
                // Ended at the bottom right, top left, and top right corners; the shot sits at (1000, 500) on screen.
                if (!Match(capture, shot, origin, Origins(1220, 100), Origins(630, 60), out found) || found != new Rectangle(1120, 570, 100, 60) ||
                    !Match(capture, shot, origin, Origins(1120, 100), Origins(570, 60), out found) || found != new Rectangle(1120, 570, 100, 60) ||
                    !Match(capture, shot, Point.Empty, Origins(220, 100), Origins(70, 60), out found) || found != expected ||
                    Match(other, shot, Point.Empty, Origins(220, 100), Origins(130, 60), out found))
                    throw new InvalidOperationException("Pinned image placement selftest failed.");
            }
        }
    }

    // A window that is never shown, found by Shift+F1 with sp.FindWindow.
    private sealed class PinServer : Form
    {
        private readonly DateTime built = BuildTime();
        internal bool Retired;

        internal PinServer()
        {
            Text = ServerTitle;
            ShowInTaskbar = false;
            CreateHandle();
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == PinMessage && !Retired)
            {
                Point corner = new Point(unchecked((int)message.WParam.ToInt64()), unchecked((int)message.LParam.ToInt64()));
                // Answered at once: StrokesPlus waits in SendMessage until this returns.
                message.Result = new IntPtr(1);
                BeginInvoke(new Action(delegate { Serve(corner); }));
                return;
            }
            base.WndProc(ref message);
        }

        private void Serve(Point corner)
        {
            Pin(corner, null);
            // A rebuilt helper: this copy stops answering, and the next capture starts the new one.
            if (BuildTime() != built) Retire();
        }

        private void Retire()
        {
            Retired = true;
            DestroyHandle();
            ExitIfIdle();
        }
    }

    private sealed class PinWindow : Form
    {
        private const int Border = 1;
        private const double MinZoom = 0.05, MaxZoom = 16;
        private readonly Bitmap image;
        private ContextMenuStrip menu;
        private double zoom = 1;
        private bool dragging;
        private Point grab;
        private Form translation;

        // imageArea: where the image goes on screen; the border goes around it.
        internal PinWindow(Bitmap image, Rectangle imageArea)
        {
            this.image = image;
            Text = WindowTitle;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true; KeyPreview = true;
            BackColor = Color.FromArgb(90, 90, 104);
            Bounds = Rectangle.Inflate(imageArea, Border, Border);
            // Building the menu took 0.3 s, longer than the rest of the start-up together: it is built
            // once the image is on screen, before the first right-click can come.
            Shown += delegate { BeginInvoke(new Action(delegate { RightClickMenu(); })); };
            MouseDown += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { dragging = true; grab = e.Location; } };
            MouseMove += delegate(object sender, MouseEventArgs e)
            {
                if (dragging) Location = new Point(Location.X + e.X - grab.X, Location.Y + e.Y - grab.Y);
            };
            MouseUp += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) dragging = false;
                else if (e.Button == MouseButtons.Right) RightClickMenu().Show(this, e.Location);
            };
            MouseWheel += delegate(object sender, MouseEventArgs e) { ZoomBy(e.Delta); };
            DoubleClick += delegate { Close(); };
            FormClosed += delegate { image.Dispose(); if (menu != null) menu.Dispose(); };
        }

        private ContextMenuStrip RightClickMenu()
        {
            if (menu != null || IsDisposed) return menu;
            menu = new ContextMenuStrip { Font = new Font("Malgun Gothic", 9.5f) };
            menu.Items.Add(Item("번역(&T)", Keys.None, delegate { TranslateImage(); }));
            menu.Items.Add(Item("복사(&C)", Keys.Control | Keys.C, delegate { CopyImage(); }));
            menu.Items.Add(Item("다른 이름으로 저장(&S)…", Keys.Control | Keys.S, delegate { SaveImage(); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Item("닫기(&X)", Keys.Escape, delegate { Close(); }));
            return menu;
        }

        private static ToolStripMenuItem Item(string label, Keys keys, EventHandler click)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(label, null, click);
            // Shown only: ProcessCmdKey handles the keys, so they work with the menu closed too.
            if (keys == Keys.Escape) item.ShortcutKeyDisplayString = "Esc";
            else if (keys != Keys.None) item.ShortcutKeyDisplayString = keys == (Keys.Control | Keys.C) ? "Ctrl+C" : "Ctrl+S";
            return item;
        }

        private static Size Outer(Size inner) { return new Size(inner.Width + Border * 2, inner.Height + Border * 2); }

        protected override CreateParams CreateParams
        {
            // A tool window: no taskbar button and no Alt+Tab entry, like StrokesPlus's.
            get { CreateParams parameters = base.CreateParams; parameters.ExStyle |= 0x80; return parameters; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            graphics.InterpolationMode = zoom == 1 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.DrawImage(image, new Rectangle(Border, Border, ClientSize.Width - Border * 2, ClientSize.Height - Border * 2));
        }

        // A notch of 120 changes the size by 12%, as StrokesPlus's Delta / 1000 did.
        private void ZoomBy(int delta)
        {
            double next = Math.Max(MinZoom, Math.Min(MaxZoom, zoom * (1 + delta / 1000.0)));
            if (next == zoom) return;
            zoom = next;
            Size before = Size, after = Outer(Zoomed(image.Size, zoom));
            Bounds = new Rectangle(ZoomOrigin(Cursor.Position, Location, before, after), after);
            Invalidate();
        }

        private void TranslateImage()
        {
            // Closed here, not by ClosePreviousPopups: its close message would wait on this thread.
            if (translation != null && !translation.IsDisposed) translation.Close();
            translation = Track(SelectionTranslator.ImagePopup(Handle, image));
            translation.Show();
        }

        private void CopyImage()
        {
            try { Clipboard.SetImage(image); }
            catch (ExternalException) { }
        }

        // The same dialog StrokesPlus opened on right-click.
        private void SaveImage()
        {
            using (SaveFileDialog dialog = new SaveFileDialog { Filter = "PNG Files (*.png)|*.png", RestoreDirectory = true,
                FileName = string.Format("Snip_{0:yyyy-MM-dd_hh-mm-ss_tt}", DateTime.Now) })
                if (dialog.ShowDialog(this) == DialogResult.OK) image.Save(dialog.FileName, ImageFormat.Png);
        }

        protected override bool ProcessCmdKey(ref Message message, Keys keys)
        {
            if (keys == Keys.Escape) { Close(); return true; }
            if (keys == (Keys.Control | Keys.C)) { CopyImage(); return true; }
            if (keys == (Keys.Control | Keys.S)) { SaveImage(); return true; }
            return base.ProcessCmdKey(ref message, keys);
        }
    }
}
