using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

// "locate-capture X,Y": the user's Shift+F1 script asks this where the capture on the clipboard was
// taken from, and passes the answer to sp.DisplayImage, which otherwise centres the capture window
// on the mouse. X,Y is where the capture ended, one of the captured area's corners; the screen
// around it is compared with the image at each of the four ways it can lie. The answer, written to
// standard output as "X,Y", is the centre sp.DisplayImage wants, one pixel down and to the right
// of the area's own so the capture stands apart from the screen under it.
internal static class CaptureLocator
{
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);

    // sp.DisplayImage pads the image by this much on each side.
    internal const int DisplayPadding = 2;
    internal const int Offset = 1;

    internal static int Run(string[] args)
    {
        Point corner;
        if (!ParsePoint(args.Length == 2 ? args[1] : null, out corner)) return 2;
        // Per-monitor physical pixels, as StrokesPlus uses for its cursor point and window positions.
        SetProcessDpiAwarenessContext(new IntPtr(-4));
        Bitmap image = FromClipboard();
        if (image == null) return 1;
        using (image)
        {
            Point centre = DisplayCentre(Place(image, corner));
            Console.Out.Write(centre.X + "," + centre.Y);
            Console.Out.Flush();
        }
        return 0;
    }

    internal static bool ParsePoint(string text, out Point point)
    {
        point = Point.Empty;
        if (text == null) return false;
        string[] parts = text.Split(',');
        int x, y;
        if (parts.Length != 2 || !int.TryParse(parts[0], out x) || !int.TryParse(parts[1], out y)) return false;
        point = new Point(x, y);
        return true;
    }

    // sp.DisplayImage sets Left = X - Width / 2 and Top = Y - Height / 2, the window being the image
    // and its padding; this puts the image itself at area's corner plus the offset.
    internal static Point DisplayCentre(Rectangle area)
    {
        int width = area.Width + DisplayPadding * 2, height = area.Height + DisplayPadding * 2;
        return new Point(area.X - DisplayPadding + Offset + width / 2, area.Y - DisplayPadding + Offset + height / 2);
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

    internal static void SelfTest()
    {
        Rectangle area = new Rectangle(0, 0, 1920, 1040);
        Point point;
        if (Fallback(new Size(200, 100), new Point(500, 500), area) != new Rectangle(300, 400, 200, 100) ||
            Fallback(new Size(200, 100), new Point(50, 1035), area) != new Rectangle(0, 935, 200, 100) ||
            Origins(100, 30)[0] != 70 || Origins(100, 30)[3] != 100 ||
            !ParsePoint("2700,-300", out point) || point != new Point(2700, -300) || ParsePoint("5", out point) || ParsePoint(null, out point))
            throw new InvalidOperationException("Capture locator selftest failed.");
        // Window = image + 4; Left = X - Width / 2 must land at area.X - 2 + 1, so the image is at area.X + 1.
        foreach (Rectangle r in new[] { new Rectangle(100, 200, 300, 150), new Rectangle(-1000, 7, 31, 17) })
        {
            Point c = DisplayCentre(r);
            int width = r.Width + 4, height = r.Height + 4;
            if (c.X - width / 2 + DisplayPadding != r.X + Offset || c.Y - height / 2 + DisplayPadding != r.Y + Offset)
                throw new InvalidOperationException("Capture locator centre selftest failed.");
        }
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
}
