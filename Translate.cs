using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

// Ctrl+Alt+T in any app: copies the selection, puts the clipboard back, and shows a Korean
// translation from Claude Code's CLI (`claude -p`, Haiku) in a popup by the mouse cursor.
// The selected text reaches Claude only through the CLI's standard input and is never logged.
internal static class SelectionTranslator
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint type);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extraInfo);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string title);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);

    internal const string PopupTitle = "claude-desktop-hotkeys 번역";
    internal const string Prompt = "다음 텍스트를 자연스러운 한국어로 번역해. 설명 없이 번역문만 출력해:\n";
    // No tools, MCP servers or saved session: a translation needs none, and startup is faster.
    internal const string ClaudeArguments = "-p --model haiku --tools \"\" --no-session-persistence --strict-mcp-config";
    private const int MaxLength = 8000;
    private const int TimeoutSeconds = 90;
    private static readonly string[] terminalProcesses = { "windowsterminal", "openconsole", "conhost", "cmd", "powershell", "pwsh",
        "mintty", "wezterm-gui", "alacritty", "hyper", "tabby", "kitty", "putty" };
    private static string clipboardWarning;

    internal sealed class Result
    {
        internal bool Ok;
        internal string Text;
        internal string Claude;
    }

    internal static int Run()
    {
        // System DPI awareness: WinForms scales the popup and the cursor shares its coordinates.
        SetProcessDpiAwarenessContext(new IntPtr(-2));
        Application.EnableVisualStyles();
        IntPtr source = GetForegroundWindow();
        string text = null, problem = null;
        try
        {
            if (!WaitForKeysUp())
                problem = "단축키가 계속 눌려 있어 선택 영역을 복사하지 못했습니다.\n키를 모두 놓고 다시 눌러 보세요.";
            else
            {
                text = Normalize(CopySelection(IsTerminal(source)));
                if (text.Length == 0) problem = "선택한 텍스트가 없습니다.\n번역할 텍스트를 드래그한 뒤 다시 눌러 보세요.";
                else if (text.Length > MaxLength)
                    problem = "선택한 텍스트가 너무 깁니다(" + text.Length.ToString("N0") + "자).\n" + MaxLength.ToString("N0") + "자 이하로 선택해 주세요.";
            }
        }
        catch (Exception error) { problem = "선택 영역을 복사하지 못했습니다.\n" + error.Message; }
        if (problem != null) Record("Not translated: " + problem.Replace("\n", " "));
        ClosePreviousPopups();
        using (TranslationPopup popup = new TranslationPopup(source, problem == null ? text : null, problem))
            Application.Run(popup);
        return 0;
    }

    // Translates a fixed sentence without UI or clipboard, to check the CLI from a terminal.
    internal static int Check()
    {
        Result result = Translate("The quick brown fox jumps over the lazy dog.", null);
        Record((result.Ok ? "Check OK: " : "Check failed: ") + result.Text.Replace("\n", " ") + " (claude: " + (result.Claude ?? "not found") + ")");
        return result.Ok ? 0 : 1;
    }

    private static bool IsDown(int key) { return (GetAsyncKeyState(key) & 0x8000) != 0; }
    // Ctrl+C sent while Alt or T is still held would reach the app as another chord.
    private static bool WaitForKeysUp()
    {
        Stopwatch timer = Stopwatch.StartNew();
        long releasedSince = -1;
        while (timer.ElapsedMilliseconds < 3000)
        {
            if (IsDown(16) || IsDown(17) || IsDown(18) || IsDown(91) || IsDown(92) || IsDown(84)) releasedSince = -1;
            else
            {
                if (releasedSince < 0) releasedSince = timer.ElapsedMilliseconds;
                if (timer.ElapsedMilliseconds - releasedSince >= 20) return true;
            }
            Thread.Sleep(5);
        }
        return false;
    }

    internal static bool IsTerminal(string processName, string className)
    {
        if (className == "ConsoleWindowClass" || className == "CASCADIA_HOSTING_WINDOW_CLASS") return true;
        return Array.IndexOf(terminalProcesses, (processName ?? "").ToLowerInvariant()) >= 0;
    }
    private static bool IsTerminal(IntPtr window)
    {
        StringBuilder className = new StringBuilder(256);
        GetClassName(window, className, className.Capacity);
        string processName = "";
        try
        {
            uint pid;
            GetWindowThreadProcessId(window, out pid);
            processName = Process.GetProcessById((int)pid).ProcessName;
        }
        catch { }
        return IsTerminal(processName, className.ToString());
    }

    private static void Tap(byte modifier, byte key, bool extended)
    {
        byte modifierScan = (byte)MapVirtualKey(modifier, 0), keyScan = (byte)MapVirtualKey(key, 0);
        uint flag = extended ? 1u : 0u;
        keybd_event(modifier, modifierScan, 0, TypeAhead.OwnKey);
        keybd_event(key, keyScan, flag, TypeAhead.OwnKey);
        keybd_event(key, keyScan, flag | 2, TypeAhead.OwnKey);
        keybd_event(modifier, modifierScan, 2, TypeAhead.OwnKey);
    }

    private static string CopySelection(bool terminal)
    {
        DataObject saved = SnapshotClipboard();
        uint before = GetClipboardSequenceNumber();
        // In a terminal Ctrl+C with nothing selected interrupts the running program; Ctrl+Insert only copies.
        if (terminal) Tap(0x11, 0x2D, true);
        else Tap(0x11, 0x43, false);
        Stopwatch wait = Stopwatch.StartNew();
        while (GetClipboardSequenceNumber() == before && wait.ElapsedMilliseconds < 800) Thread.Sleep(10);
        // Nothing was copied, so the clipboard is untouched and needs no restoring.
        if (GetClipboardSequenceNumber() == before) return "";
        // Some apps add their formats one after another; let them finish.
        Thread.Sleep(40);
        string text = "";
        try { if (Clipboard.ContainsText(TextDataFormat.UnicodeText)) text = Clipboard.GetText(TextDataFormat.UnicodeText); }
        finally { RestoreClipboard(saved); }
        return text;
    }

    // Formats whose data can be put back as is. Others (OLE objects, private handles) are skipped.
    private static DataObject SnapshotClipboard()
    {
        IDataObject current = Clipboard.GetDataObject();
        if (current == null) return null;
        DataObject copy = new DataObject();
        int kept = 0;
        foreach (string format in current.GetFormats(false))
            try
            {
                object data = current.GetData(format, false);
                if (data is Stream || data is string || data is string[] || data is Image)
                {
                    copy.SetData(format, false, data);
                    kept++;
                }
            }
            catch { }
        return kept > 0 ? copy : null;
    }

    private static void RestoreClipboard(DataObject saved)
    {
        try
        {
            if (saved == null) { Clipboard.Clear(); return; }
            // The original entry is already in Win+V history; do not add it a second time.
            saved.SetData("CanIncludeInClipboardHistory", false, new MemoryStream(BitConverter.GetBytes(0)));
            Clipboard.SetDataObject(saved, true, 10, 50);
        }
        catch (Exception error) { clipboardWarning = "원래 클립보드 내용을 복원하지 못했습니다: " + error.Message; }
    }

    internal static string Normalize(string text)
    {
        return (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
    }

    private static void ClosePreviousPopups()
    {
        for (int tries = 0; tries < 20; tries++)
        {
            IntPtr old = FindWindow(null, PopupTitle);
            if (old == IntPtr.Zero) return;
            PostMessage(old, 0x0010, IntPtr.Zero, IntPtr.Zero);
            Thread.Sleep(25);
        }
    }

    // PATH as this process sees it may predate a recent install, so the saved user and
    // machine PATH follow, then the usual install folders and the Claude app's own copy.
    internal static string FindClaude()
    {
        List<string> folders = new List<string>();
        foreach (EnvironmentVariableTarget target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            string path = null;
            try { path = Environment.GetEnvironmentVariable("PATH", target); } catch { }
            if (path == null) continue;
            foreach (string entry in path.Split(';'))
            {
                string folder = Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"'));
                if (folder.Length > 0 && !folders.Exists(delegate(string known) { return string.Equals(known, folder, StringComparison.OrdinalIgnoreCase); }))
                    folders.Add(folder);
            }
        }
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local\\bin"));
        folders.Add(Path.Combine(appData, "npm"));
        foreach (string folder in folders)
            foreach (string extension in new[] { ".exe", ".cmd", ".bat" })
                try
                {
                    string candidate = Path.Combine(folder, "claude" + extension);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
        string newest = null;
        DateTime newestTime = DateTime.MinValue;
        foreach (string bundled in BundledFolders(appData, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)))
            try
            {
                if (Directory.Exists(bundled))
                    foreach (string candidate in Directory.GetFiles(bundled, "claude.exe", SearchOption.AllDirectories))
                    {
                        DateTime written = File.GetLastWriteTimeUtc(candidate);
                        if (written > newestTime) { newest = candidate; newestTime = written; }
                    }
            }
            catch { }
        return newest;
    }

    // The Claude app is an MSIX package: %APPDATA%\Claude is its virtual view, seen only inside
    // the app, and an app update can leave nothing there for other processes. The files are in
    // the package's own folder, which every process sees.
    internal static List<string> BundledFolders(string appData, string localAppData)
    {
        List<string> folders = new List<string> { Path.Combine(appData, "Claude\\claude-code") };
        try
        {
            string packages = Path.Combine(localAppData, "Packages");
            if (Directory.Exists(packages))
                foreach (string package in Directory.GetDirectories(packages, "Claude_*"))
                    folders.Add(Path.Combine(package, "LocalCache\\Roaming\\Claude\\claude-code"));
        }
        catch { }
        return folders;
    }

    // A long-lived token from `claude setup-token`, kept apart from the CLI's own login: that login's
    // refresh token rotates, and another process refreshing it first wipes the CLI's copy.
    internal static string TokenPath()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "claude-desktop-hotkeys\\claude-token.txt");
    }

    internal static string ReadToken(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            // A token has no whitespace; a copy from a terminal that wrapped its long line may.
            string token = Regex.Replace(File.ReadAllText(path), @"\s+", "");
            return token.Length > 0 ? token : null;
        }
        catch { return null; }
    }

    internal static ProcessStartInfo StartInfo(string claude, string token = null)
    {
        bool script = claude.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || claude.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
        ProcessStartInfo info = script
            ? new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", "/d /s /c \"\"" + claude + "\" " + ClaudeArguments + "\"")
            : new ProcessStartInfo(claude, ClaudeArguments);
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.StandardOutputEncoding = new UTF8Encoding(false);
        info.StandardErrorEncoding = new UTF8Encoding(false);
        // An empty folder keeps any project's CLAUDE.md and settings out of the translation.
        info.WorkingDirectory = Path.GetTempPath();
        // Claude Code refuses to start inside what looks like another Claude Code session.
        info.EnvironmentVariables.Remove("CLAUDECODE");
        // The CLI takes this over its stored login; set for this process alone.
        if (token != null) info.EnvironmentVariables["CLAUDE_CODE_OAUTH_TOKEN"] = token;
        return info;
    }

    internal static Result Translate(string text, Action<Process> started)
    {
        Result result = new Result { Claude = FindClaude() };
        if (result.Claude == null)
        {
            result.Text = "Claude Code CLI(claude)를 찾을 수 없습니다.\n설치한 뒤 터미널에서 claude를 한 번 실행해 로그인하세요.\n설치: PowerShell에서 irm https://claude.ai/install.ps1 | iex";
            return result;
        }
        string tokenPath = TokenPath(), token = ReadToken(tokenPath);
        using (Process process = new Process { StartInfo = StartInfo(result.Claude, token) })
        {
            try { process.Start(); }
            catch (Exception error)
            {
                result.Text = "claude를 실행하지 못했습니다.\n" + result.Claude + "\n" + error.Message;
                return result;
            }
            if (started != null) started(process);
            StringBuilder errors = new StringBuilder();
            process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs line) { if (line.Data != null) lock (errors) errors.AppendLine(line.Data); };
            process.BeginErrorReadLine();
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            // UTF-8 bytes straight to the pipe; the selection never appears on a command line.
            try
            {
                byte[] input = new UTF8Encoding(false).GetBytes(Prompt + text);
                process.StandardInput.BaseStream.Write(input, 0, input.Length);
                process.StandardInput.Close();
            }
            catch (IOException) { }
            if (!process.WaitForExit(TimeoutSeconds * 1000))
            {
                KillTree(process);
                result.Text = TimeoutSeconds + "초 안에 응답이 없어 번역을 멈췄습니다.";
                return result;
            }
            process.WaitForExit();
            string translated = Normalize(output.Result), errorText;
            lock (errors) errorText = errors.ToString();
            if (process.ExitCode == 0 && translated.Length > 0) { result.Ok = true; result.Text = translated; }
            else if (process.ExitCode == 0) result.Text = "번역 결과가 비어 있습니다.";
            else result.Text = FailureMessage(process.ExitCode, translated, errorText, result.Claude, tokenPath, token != null);
            return result;
        }
    }

    internal static string FailureMessage(int exitCode, string output, string error, string claude, string tokenPath, bool usedToken)
    {
        string all = (error ?? "") + "\n" + (output ?? "");
        if (Regex.IsMatch(all, @"not logged in|run /login|invalid api key|please log ?in|failed to authenticate|oauth session expired|invalid bearer token|token (has )?expired|revoked", RegexOptions.IgnoreCase))
            return (usedToken ? "저장한 Claude 토큰이 만료됐거나 취소됐습니다.\n" : "Claude Code CLI에 로그인되어 있지 않습니다.\n") +
                "터미널에서 다음을 실행해 1년짜리 토큰을 발급받고,\n\"" + claude + "\" setup-token\n" +
                "출력된 토큰을 다음 파일에 저장하세요.\n" + tokenPath;
        string detail = Normalize(string.IsNullOrWhiteSpace(error) ? output : error);
        if (detail.Length > 400) detail = detail.Substring(0, 400) + "…";
        return "번역하지 못했습니다(claude 종료 코드 " + exitCode + ")." + (detail.Length > 0 ? "\n" + detail : "");
    }

    internal static void KillTree(Process process)
    {
        try
        {
            if (process.HasExited) return;
            // A .cmd shim's node process is a child of cmd.exe; kill the whole tree.
            using (Process kill = Process.Start(new ProcessStartInfo("taskkill", "/T /F /PID " + process.Id) { UseShellExecute = false, CreateNoWindow = true }))
                kill.WaitForExit(3000);
        }
        catch { }
    }

    // Below and right of the cursor; flipped to the other side at the screen's edge.
    internal static Point Place(Point cursor, Size size, Rectangle area, int gap)
    {
        int x = cursor.X + gap, y = cursor.Y + gap + gap / 2;
        if (x + size.Width > area.Right) x = Math.Max(area.Left, Math.Min(cursor.X - gap - size.Width, area.Right - size.Width));
        if (y + size.Height > area.Bottom) y = cursor.Y - gap - size.Height;
        if (y < area.Top) y = Math.Max(area.Top, area.Bottom - size.Height);
        return new Point(Math.Max(area.Left, x), y);
    }

    private static void Record(string message)
    {
        try
        {
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "last-result-translate.txt"),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine);
        }
        catch { }
    }

    internal static void SelfTest()
    {
        Rectangle area = new Rectangle(0, 0, 1920, 1040);
        Size size = new Size(440, 200);
        Point near = Place(new Point(500, 300), size, area, 16);
        Point corner = Place(new Point(1910, 1030), size, area, 16);
        Point top = Place(new Point(1910, 5), new Size(440, 1000), area, 16);
        if (near != new Point(516, 324) || corner != new Point(1910 - 16 - 440, 1030 - 16 - 200) ||
            !area.Contains(new Rectangle(top, new Size(440, 1000))))
            throw new InvalidOperationException("Translation popup placement selftest failed.");
        string login = FailureMessage(1, "Not logged in · Please run /login", "", "C:\\x\\claude.exe", "C:\\t\\claude-token.txt", false);
        string expired = FailureMessage(1, "", "Failed to authenticate: OAuth session expired and could not be refreshed", "claude", "C:\\t\\claude-token.txt", true);
        if (!login.Contains("로그인") || !login.Contains("\"C:\\x\\claude.exe\" setup-token") || !login.Contains("C:\\t\\claude-token.txt") ||
            !expired.StartsWith("저장한 Claude 토큰") ||
            !FailureMessage(1, "", "Error: overloaded\n", "claude", "t", false).Contains("종료 코드 1).\nError: overloaded") ||
            FailureMessage(2, new string('a', 900), "", "claude", "t", false).Length > 460)
            throw new InvalidOperationException("Translation failure message selftest failed.");
        ProcessStartInfo exe = StartInfo("C:\\Tools\\claude.exe"), shim = StartInfo("C:\\Users\\a b\\AppData\\Roaming\\npm\\claude.cmd");
        if (exe.Arguments != ClaudeArguments || !exe.FileName.EndsWith("claude.exe") || !exe.RedirectStandardInput ||
            shim.Arguments != "/d /s /c \"\"C:\\Users\\a b\\AppData\\Roaming\\npm\\claude.cmd\" " + ClaudeArguments + "\"" ||
            exe.StandardOutputEncoding.CodePage != 65001)
            throw new InvalidOperationException("Translation command line selftest failed.");
        string fakeLocal = Path.Combine(Path.GetTempPath(), "claude-desktop-hotkeys-selftest-local");
        try
        {
            Directory.CreateDirectory(Path.Combine(fakeLocal, "Packages\\Claude_pzs8sxrjxfjjc"));
            Directory.CreateDirectory(Path.Combine(fakeLocal, "Packages\\Other_app"));
            List<string> bundled = BundledFolders("C:\\Roaming", fakeLocal);
            if (bundled.Count != 2 || bundled[0] != "C:\\Roaming\\Claude\\claude-code" ||
                bundled[1] != Path.Combine(fakeLocal, "Packages\\Claude_pzs8sxrjxfjjc\\LocalCache\\Roaming\\Claude\\claude-code") ||
                BundledFolders("C:\\Roaming", fakeLocal + ".missing").Count != 1)
                throw new InvalidOperationException("Translation CLI folder selftest failed.");
        }
        finally { try { Directory.Delete(fakeLocal, true); } catch { } }
        string tokenFile = Path.Combine(Path.GetTempPath(), "claude-desktop-hotkeys-selftest-token.txt");
        try
        {
            File.WriteAllText(tokenFile, "  sk-test- tok\r\nen \r\n");
            string token = ReadToken(tokenFile);
            ProcessStartInfo withToken = StartInfo("C:\\Tools\\claude.exe", token);
            File.WriteAllText(tokenFile, " \r\n");
            if (token != "sk-test-token" || withToken.EnvironmentVariables["CLAUDE_CODE_OAUTH_TOKEN"] != "sk-test-token" ||
                ReadToken(tokenFile) != null || ReadToken(tokenFile + ".missing") != null ||
                exe.EnvironmentVariables["CLAUDE_CODE_OAUTH_TOKEN"] != Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN"))
                throw new InvalidOperationException("Translation token selftest failed.");
        }
        finally { try { File.Delete(tokenFile); } catch { } }
        if (Normalize(" a\r\nb\rc \n") != "a\nb\nc" || !IsTerminal("WindowsTerminal", "") || !IsTerminal("", "ConsoleWindowClass") ||
            IsTerminal("chrome", "Chrome_WidgetWin_1") || IsTerminal("notepad", "Notepad"))
            throw new InvalidOperationException("Translation text or terminal selftest failed.");
    }

    private sealed class TranslationPopup : Form
    {
        private readonly IntPtr source;
        private readonly string text;
        private readonly float scale;
        private readonly Point anchor;
        private readonly Label status;
        private readonly TextBox body;
        private readonly Button copy, close;
        private readonly System.Windows.Forms.Timer ticker = new System.Windows.Forms.Timer { Interval = 250 };
        private readonly Stopwatch elapsed = Stopwatch.StartNew();
        private string translation;
        private Process running;
        private volatile bool finished;

        internal TranslationPopup(IntPtr source, string text, string problem)
        {
            this.source = source;
            this.text = text;
            Text = PopupTitle;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(35, 35, 40); ForeColor = Color.White;
            Font = new Font("Malgun Gothic", 10.5f);
            using (Graphics graphics = CreateGraphics()) scale = graphics.DpiX / 96f;
            anchor = Cursor.Position;
            status = new Label { AutoSize = false, ForeColor = Color.FromArgb(165, 165, 178), Font = new Font("Malgun Gothic", 9f), TextAlign = ContentAlignment.MiddleLeft };
            body = new TextBox { Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, WordWrap = true,
                BackColor = BackColor, ForeColor = Color.White };
            copy = MakeButton("복사");
            close = MakeButton("닫기 (Esc)");
            copy.Enabled = false;
            copy.Click += delegate { CopyTranslation(); };
            close.Click += delegate { CloseBack(); };
            Controls.AddRange(new Control[] { status, body, copy, close });
            Paint += delegate(object sender, PaintEventArgs e)
            {
                using (Pen border = new Pen(Color.FromArgb(85, 85, 98))) e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
            };
            FormClosing += delegate { finished = true; ticker.Stop(); Process process = running; if (process != null) KillTree(process); };
            if (problem != null) ShowProblem(problem);
            else
            {
                status.Text = "Claude Haiku로 번역하는 중";
                body.Text = "번역 중…";
                body.ForeColor = Color.FromArgb(165, 165, 178);
                ticker.Tick += delegate { status.Text = "Claude Haiku로 번역하는 중 · " + (int)elapsed.Elapsed.TotalSeconds + "초"; };
                ticker.Start();
            }
            Fit();
        }

        private int S(int pixels) { return (int)Math.Round(pixels * scale); }

        private Button MakeButton(string label)
        {
            Button button = new Button { Text = label, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(58, 58, 66), ForeColor = Color.White,
                Font = new Font("Malgun Gothic", 9f), UseVisualStyleBackColor = false };
            button.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 104);
            return button;
        }

        private void Fit()
        {
            int pad = S(12), width = S(460), inner = width - pad * 2, statusHeight = S(20), buttonHeight = S(28), gap = S(8);
            Rectangle area = Screen.FromPoint(anchor).WorkingArea;
            Size measured = TextRenderer.MeasureText(body.Text.Length > 0 ? body.Text : " ", body.Font, new Size(inner - S(18), int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
            int maxBody = area.Height * 6 / 10, bodyHeight = Math.Max(S(24), measured.Height + S(4));
            body.ScrollBars = bodyHeight > maxBody ? ScrollBars.Vertical : ScrollBars.None;
            bodyHeight = Math.Min(bodyHeight, maxBody);
            status.SetBounds(pad, pad, inner, statusHeight);
            body.SetBounds(pad, pad + statusHeight + gap / 2, inner, bodyHeight);
            int buttonsTop = body.Bottom + gap + S(2), closeWidth = S(92), copyWidth = S(64);
            close.SetBounds(width - pad - closeWidth, buttonsTop, closeWidth, buttonHeight);
            copy.SetBounds(close.Left - S(6) - copyWidth, buttonsTop, copyWidth, buttonHeight);
            Size size = new Size(width, buttonsTop + buttonHeight + pad);
            Bounds = new Rectangle(Place(anchor, size, area, S(16)), size);
            Invalidate();
        }

        private void ShowProblem(string problem)
        {
            finished = true;
            ticker.Stop();
            status.Text = "번역 오류";
            body.ForeColor = Color.FromArgb(255, 170, 160);
            body.Text = problem.Replace("\n", Environment.NewLine);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            TakeForeground();
            body.Focus();
            body.Select(0, 0);
            if (text == null) return;
            Thread worker = new Thread(delegate()
            {
                Result result = Translate(text, delegate(Process process) { running = process; if (finished) KillTree(process); });
                running = null;
                if (finished) return;
                try { BeginInvoke(new Action(delegate { ShowResult(result); })); } catch (InvalidOperationException) { }
            }) { IsBackground = true };
            worker.Start();
        }

        private void ShowResult(Result result)
        {
            if (finished || IsDisposed) return;
            double seconds = elapsed.Elapsed.TotalSeconds;
            Record((result.Ok ? "Translated " + text.Length + " -> " + result.Text.Length + " chars" : "Failed: " + result.Text.Replace("\n", " ")) +
                " in " + seconds.ToString("0.0") + " s (claude: " + (result.Claude ?? "not found") + ")" +
                (clipboardWarning != null ? "; " + clipboardWarning : ""));
            if (!result.Ok) { ShowProblem(result.Text); Fit(); return; }
            finished = true;
            ticker.Stop();
            translation = result.Text;
            status.Text = "Claude Haiku · " + seconds.ToString("0.0") + "초" + (clipboardWarning != null ? " · " + clipboardWarning : "");
            body.ForeColor = Color.White;
            body.Text = translation.Replace("\n", Environment.NewLine);
            copy.Enabled = true;
            Fit();
            body.Select(0, 0);
        }

        private void CopyTranslation()
        {
            if (translation == null) return;
            try { Clipboard.SetText(translation, TextDataFormat.UnicodeText); copy.Text = "복사됨"; }
            catch { copy.Text = "실패"; }
        }

        protected override bool ProcessCmdKey(ref Message message, Keys keys)
        {
            if (keys == Keys.Escape) { CloseBack(); return true; }
            if (keys == (Keys.Control | Keys.A)) { body.SelectAll(); return true; }
            // Ctrl+C with nothing selected copies the whole translation.
            if (keys == (Keys.Control | Keys.C) && body.SelectionLength == 0 && translation != null) { CopyTranslation(); return true; }
            return base.ProcessCmdKey(ref message, keys);
        }

        // Back to the window the text came from, unless the user has already moved elsewhere.
        private void CloseBack()
        {
            if (GetForegroundWindow() == Handle && source != IntPtr.Zero && IsWindow(source)) SetForegroundWindow(source);
            Close();
        }

        // A background process may not take the foreground; an input event of its own allows it.
        private void TakeForeground()
        {
            Activate();
            if (GetForegroundWindow() == Handle) return;
            keybd_event(0x12, 0, 0, TypeAhead.OwnKey);
            SetForegroundWindow(Handle);
            keybd_event(0x12, 0, 2, TypeAhead.OwnKey);
        }
    }
}
