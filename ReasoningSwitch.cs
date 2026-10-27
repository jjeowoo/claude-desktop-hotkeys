using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;

// No network, clipboard access, chat submission, or background hotkey hook, except that
// "translate" (Translate.cs) uses the clipboard and runs Claude Code's CLI on the selection.
// StrokesPlus launches this only when the user presses the configured hotkey.
internal static class ReasoningSwitch
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out System.Drawing.Point point);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint processId);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extraInfo);
    private static IntPtr targetWindow;
    private static string app = "";
    private static string command = "";
    private static AutomationElement targetRoot;
    private static System.Windows.Rect chatInputBounds = System.Windows.Rect.Empty;
    private static System.Windows.Rect chatInputWindowBounds = System.Windows.Rect.Empty;
    private static StringBuilder trace = new StringBuilder();
    private static Stopwatch operationTimer = Stopwatch.StartNew();
    private const string BuildName = "46-claude-desktop-hotkeys";
    // Anchored: sidebar session titles may also contain the model name.
    private const string ClaudeModelPattern = @"^\s*(?:(?:Model|모델)\s*:?\s*(?:Claude\s+)?Opus\s+5\.5\b|(?:Claude\s+)?Opus\s+5\.5\s*$)";
    // Not ListItem: Claude's Chat tab exposes conversation text as list items.
    private static readonly ControlType[] choiceTypes = {
        ControlType.RadioButton, ControlType.MenuItem,
        ControlType.CheckBox, ControlType.Button
    };

    internal static int Run(string[] args)
    {
        if (args.Length == 1 && args[0] == "--selftest")
        {
            try { return SelfTest(); }
            catch (Exception error)
            {
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selftest-error.txt"), error.ToString());
                return 1;
            }
        }
        if (args.Length == 3 && args[0] == "--refresh-ime") return RefreshTextInput(args[1], args[2]);
        if (args.Length == 1 && args[0] == "translate") return SelectionTranslator.Run();
        if (args.Length == 1 && args[0] == "--translate-check") return SelectionTranslator.Check();
        if (args.Length != 1 || (args[0] != "up" && args[0] != "down" && args[0] != "input" && args[0] != "status" && args[0] != "markdown" && args[0] != "tasks")) return 2;
        // Screenshot coordinates and cursor coordinates must use physical pixels.
        SetProcessDpiAwarenessContext(new IntPtr(-4));
        bool ownsLock;
        using (Mutex gate = new Mutex(true, "Local\\StrokesPlusReasoningSwitch", out ownsLock))
        {
            if (!ownsLock) return 0;
            try
            {
                targetWindow = GetForegroundWindow();
                uint pid;
                GetWindowThreadProcessId(targetWindow, out pid);
                app = Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant();
                if (app != "chatgpt" && app != "claude") return 0;
                if ((args[0] == "status" || args[0] == "markdown" || args[0] == "tasks") && app != "claude") return 0;
                command = args[0];
                // Clicks and the Right arrow key need the chord released first; a transient release
                // can occur between chords. S, D and B only press controls through UI Automation, which
                // held keys do not change, so they start at once instead of giving up after 3 s.
                if (args[0] != "status" && args[0] != "markdown" && args[0] != "tasks")
                {
                    WaitForInputRelease();
                    trace.AppendLine("Initial key release at " + operationTimer.ElapsedMilliseconds + " ms.");
                }
                // These runs move focus away from the chat input; keep what is typed meanwhile.
                if (args[0] != "input") TypeAhead.Hold();
                int direction = args[0] == "up" ? 1 : -1;
                AutomationElement root = AutomationElement.FromHandle(targetWindow);
                targetRoot = root;
                if (args[0] == "input") { FocusInput(root); return 0; }
                if (args[0] == "tasks") { ToggleTasksPane(root); return 0; }
                if (args[0] == "markdown") { ToggleMarkdownView(root); return 0; }
                Stopwatch inputTimer = Stopwatch.StartNew();
                RememberChatInput(root);
                trace.AppendLine("Chat input lookup=" + inputTimer.ElapsedMilliseconds + " ms.");
                if (args[0] == "status") { OpenArchivedSessions(root); return 0; }
                if (app == "chatgpt") SwitchCodex(root, direction);
                else SwitchClaude(root, direction);
                return 0;
            }
            catch (OperationCanceledException error)
            {
                trace.AppendLine(error.Message);
                Record("Stopped: input focus changed before the slider click.");
                return 0;
            }
            catch (Exception error)
            {
                WriteDiagnostic(error);
                Record("ERROR: " + error.Message);
                ShowNotice((args[0] == "input" ? "입력란으로 이동하지 못했습니다." :
                    args[0] == "status" ? "보관된 세션을 열지 못했습니다." :
                    args[0] == "markdown" ? "Markdown 미리보기/소스를 전환하지 못했습니다." :
                    args[0] == "tasks" ? "백그라운드 작업 창을 전환하지 못했습니다." : "추론 전환을 확인하지 못했습니다.") +
                    "\nReasoningHotkeys의 last-result.txt를 확인하세요.");
                return 1;
            }
            finally
            {
                if (focusTouched && targetWindow != IntPtr.Zero)
                    try
                    {
                        // Held keys must wait for the renewed IME link, so wait for the refresh.
                        using (Process refresh = Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName,
                            "--refresh-ime " + targetWindow.ToInt64() + " 0") { UseShellExecute = false, CreateNoWindow = true }))
                        {
                            // This run came from the hotkey, so it may hand its right to take the
                            // foreground on; without it the refresh must fake an input event.
                            AllowSetForegroundWindow((uint)refresh.Id);
                            refresh.WaitForExit(2000);
                        }
                    }
                    catch { }
                gate.ReleaseMutex();
                try
                {
                    int typed = TypeAhead.Release();
                    if (typed > 0 && app != null)
                        File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "last-action-" + app + ".txt"),
                            "Keys held during the run and typed again: " + typed + Environment.NewLine);
                }
                catch { }
            }
        }
    }

    private static bool focusTouched;
    // UI Automation's SetFocus leaves Chromium's link to the IME stale until the window is
    // activated again: Hangul then composes in a box at the screen's top-left, and each
    // first character lands only with the next one. Activating an invisible window and
    // coming straight back renews it in about 0.1 s; focus stays where it was. Done from
    // the helper itself it did not hold after the session status change, so a second,
    // short-lived process does it once the helper has exited. It never pulls the window
    // back to the front if the user has moved to another one.
    private static int RefreshTextInput(string window, string parent)
    {
        if (parent != "0")
            try { Process.GetProcessById(int.Parse(parent)).WaitForExit(2000); } catch { }
        targetWindow = new IntPtr(long.Parse(window));
        uint pid;
        GetWindowThreadProcessId(targetWindow, out pid);
        try { app = Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant(); } catch { return 0; }
        string result;
        if (GetForegroundWindow() != targetWindow) result = "IME refresh skipped: the window is no longer in front.";
        else result = BounceActivation();
        try { File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "last-action-" + app + ".txt"), result + Environment.NewLine); } catch { }
        return 0;
    }
    private static string BounceActivation()
    {
        bool plain, own, back, retried = false;
        using (System.Windows.Forms.Form bounce = new System.Windows.Forms.Form())
        {
            bounce.ShowInTaskbar = false;
            bounce.FormBorderStyle = System.Windows.Forms.FormBorderStyle.None;
            bounce.StartPosition = System.Windows.Forms.FormStartPosition.Manual;
            bounce.Location = new System.Drawing.Point(-32000, -32000);
            bounce.Size = new System.Drawing.Size(1, 1);
            bounce.Opacity = 0;
            bounce.Show();
            System.Windows.Forms.Application.DoEvents();
            plain = SetForegroundWindow(bounce.Handle) && GetForegroundWindow() == bounce.Handle;
            if (!plain)
            {
                // A background process may not take the foreground; an input event of its own allows
                // it. A whole Shift tap, sent before the switch: an Alt pressed in Claude and released
                // in the hidden window left Claude eating the next key (2026-10-19).
                TapShift();
                SetForegroundWindow(bounce.Handle);
            }
            System.Windows.Forms.Application.DoEvents();
            own = GetForegroundWindow() == bounce.Handle;
            SetForegroundWindow(targetWindow);
            System.Windows.Forms.Application.DoEvents();
            back = GetForegroundWindow() == targetWindow;
            if (!back && own)
            {
                // Windows can refuse the way back too; never leave the hidden window in front.
                TapShift();
                SetForegroundWindow(targetWindow);
                System.Windows.Forms.Application.DoEvents();
                back = GetForegroundWindow() == targetWindow;
                if (back) retried = true;
            }
        }
        return "IME refresh: plain switch=" + plain + ", own window in front=" + own + ", back=" + back + (retried ? " (second try)" : "");
    }
    // Shift alone types nothing, opens no menu and does not switch Hangul.
    private static void TapShift()
    {
        keybd_event(0x10, 0, 0, TypeAhead.OwnKey);
        keybd_event(0x10, 0, 2, TypeAhead.OwnKey);
    }
    private static bool IsDown(int key) { return (GetAsyncKeyState(key) & 0x8000) != 0; }
    private static bool InputHeld()
    {
        return IsDown(17) || IsDown(18) || IsDown(16) || IsDown(91) || IsDown(92) || IsDown(38) || IsDown(40) || IsDown(191) || IsDown(83) || IsDown(68);
    }
    private static void WaitForInputRelease()
    {
        Stopwatch timer = Stopwatch.StartNew();
        long releasedSince = -1;
        while (timer.ElapsedMilliseconds < 3000)
        {
            EnsureForeground();
            if (InputHeld()) releasedSince = -1;
            else
            {
                if (releasedSince < 0) releasedSince = timer.ElapsedMilliseconds;
                if (timer.ElapsedMilliseconds - releasedSince >= 20) return;
            }
            Thread.Sleep(5);
        }
        throw new InvalidOperationException("Release Ctrl, Alt and the arrow key, then press the hotkey again.");
    }
    private static void EnsureForeground()
    {
        IntPtr other;
        long away = ForegroundAway(delegate { return GetForegroundWindow(); }, targetWindow,
            delegate { return operationTimer.ElapsedMilliseconds; }, delegate { Thread.Sleep(5); }, out other);
        if (away == 0) return;
        if (away < 0) throw new InvalidOperationException("Active app changed; operation stopped. Foreground: " + DescribeWindow(other));
        trace.AppendLine("Foreground left for " + away + " ms: " + DescribeWindow(other));
    }
    // An activation in progress can leave the foreground empty or on another
    // window for a moment. Wait briefly for the target to return; nothing is
    // clicked while another window is in front. Returns 0 if the target never
    // left, the time away if it returned, and -1 if it did not return.
    private static long ForegroundAway(Func<IntPtr> read, IntPtr target, Func<long> elapsed, Action poll, out IntPtr other)
    {
        other = IntPtr.Zero;
        IntPtr current = read();
        if (current == target) return 0;
        long started = elapsed();
        do
        {
            other = current;
            if (elapsed() - started >= 300) return -1;
            poll();
            current = read();
        } while (current != target);
        return Math.Max(1, elapsed() - started);
    }
    private static string DescribeWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "none";
        // Process and window class only; a title can contain chat or file names.
        string process = "unknown";
        try { uint pid; GetWindowThreadProcessId(hwnd, out pid); process = Process.GetProcessById((int)pid).ProcessName; } catch { }
        StringBuilder name = new StringBuilder(256);
        GetClassName(hwnd, name, name.Capacity);
        return "process=" + process + ", class=" + name;
    }
    private static void ForegroundSelfTest()
    {
        // Replay a steady target, a brief empty foreground, a brief other
        // window, and a window that stays in front, without any real window.
        IntPtr target = new IntPtr(1), stranger = new IntPtr(7);
        for (int scenario = 0; scenario < 4; scenario++)
        {
            long clock = 0;
            int test = scenario;
            IntPtr other;
            long away = ForegroundAway(delegate
            {
                if (test == 0 || (test != 3 && clock >= 100)) return target;
                return test == 1 ? IntPtr.Zero : stranger;
            }, target, delegate { return clock; }, delegate { clock += 5; }, out other);
            bool passed = test == 0 ? away == 0 && clock == 0 :
                test == 3 ? away == -1 && clock >= 300 && other == stranger :
                away == 100 && other == (test == 1 ? IntPtr.Zero : stranger);
            if (!passed) throw new InvalidOperationException("Foreground wait regression failed: scenario " + test);
        }
    }
    private static AutomationElement[] Elements(AutomationElement root, ControlType type)
    {
        Stopwatch queryTimer = Stopwatch.StartNew();
        System.Collections.Generic.List<AutomationElement> result = new System.Collections.Generic.List<AutomationElement>();
        {
            CacheRequest cache = new CacheRequest();
            cache.Add(AutomationElement.NameProperty);
            cache.Add(AutomationElement.IsOffscreenProperty);
            cache.Add(AutomationElement.IsEnabledProperty);
            cache.TreeScope = TreeScope.Element;
            using (cache.Activate())
            {
                AutomationElementCollection collection = root.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, type));
                foreach (AutomationElement element in collection)
                    try { if (!element.Cached.IsOffscreen && element.Cached.IsEnabled) result.Add(element); } catch { }
            }
        }
        trace.AppendLine("UIA " + type.ProgrammaticName + " lookup=" + queryTimer.ElapsedMilliseconds + " ms; visible=" + result.Count);
        return result.ToArray();
    }
    private static string Name(AutomationElement element)
    {
        try { return element.Cached.Name ?? ""; } catch { try { return element.Current.Name ?? ""; } catch { return ""; } }
    }
    private static string Describe(AutomationElement element)
    {
        string text = Name(element);
        foreach (AutomationElement child in Elements(element, ControlType.Text)) text += " " + Name(child);
        return text;
    }
    private static AutomationElement ModelButton(AutomationElement root, string pattern)
    {
        return ModelButton(Elements(root, ControlType.Button), pattern);
    }
    private static AutomationElement ModelButton(AutomationElement[] buttons, string pattern)
    {
        AutomationElement found = null;
        foreach (AutomationElement button in buttons)
        {
            string name = Name(button);
            bool matches = Regex.IsMatch(name, pattern, RegexOptions.IgnoreCase);
            if (!matches && (name.Length == 0 || Regex.IsMatch(name, @"model|effort|모델|추론", RegexOptions.IgnoreCase)))
                matches = Regex.IsMatch(Describe(button), pattern, RegexOptions.IgnoreCase);
            if (!matches) continue;
            if (found != null) throw new InvalidOperationException("More than one matching model control; no action taken.");
            found = button;
        }
        return found;
    }
    private static int Effort(string text)
    {
        if (Regex.IsMatch(text, @"\b(extra[\s-]*high|xhigh|엑스트라)\b|매우\s*높음|아주\s*높음", RegexOptions.IgnoreCase)) return 2;
        if (Regex.IsMatch(text, @"\bhigh\b|높음", RegexOptions.IgnoreCase)) return 1;
        if (Regex.IsMatch(text, @"\bmedium\b|중간|보통", RegexOptions.IgnoreCase)) return 0;
        return -1;
    }
    private static bool EffortOption(string text, int value)
    {
        string[] patterns = {
            @"^(medium|중간|보통)(\b|\s|$)",
            @"^(high|높음)(\b|\s|$)",
            @"^(extra[\s-]*high|xhigh|엑스트라|매우\s*높음|아주\s*높음)(\b|\s|$)"
        };
        return Regex.IsMatch(text.Trim(), patterns[value], RegexOptions.IgnoreCase);
    }
    private static bool IsEffortChoice(string name)
    {
        return EffortOption(name, 0) || EffortOption(name, 1) || EffortOption(name, 2);
    }
    private static int NextClaude(int current, int direction)
    {
        if (current < 0 || current > 5) throw new InvalidOperationException("Claude's current effort is not one of Low, Medium, High, Extra, Max, or Ultracode.");
        return Math.Max(0, Math.Min(5, current + direction));
    }
    private static int ClaudeEffort(string text)
    {
        if (Regex.IsMatch(text, @"\bultra[\s-]*code\b|울트라[\s-]*코드", RegexOptions.IgnoreCase)) return 5;
        if (Regex.IsMatch(text, @"\b(max|maximum)\b|최대", RegexOptions.IgnoreCase)) return 4;
        if (Regex.IsMatch(text, @"\b(extra(?:[\s-]*high)?|xhigh|엑스트라)\b|매우\s*높음|아주\s*높음", RegexOptions.IgnoreCase)) return 3;
        if (Regex.IsMatch(text, @"\bhigh\b|높음", RegexOptions.IgnoreCase)) return 2;
        if (Regex.IsMatch(text, @"\bmedium\b|중간|보통", RegexOptions.IgnoreCase)) return 1;
        if (Regex.IsMatch(text, @"\blow\b|낮음", RegexOptions.IgnoreCase)) return 0;
        return -1;
    }
    private static int GptEffort(string text)
    {
        if (Regex.IsMatch(text, @"\bultra\b|울트라", RegexOptions.IgnoreCase)) return 4;
        if (Regex.IsMatch(text, @"\b(extra[\s-]*high|xhigh)\b|매우\s*높음|아주\s*높음", RegexOptions.IgnoreCase)) return 3;
        if (Regex.IsMatch(text, @"\bhigh\b|높음", RegexOptions.IgnoreCase)) return 2;
        if (Regex.IsMatch(text, @"\bmedium\b|중간|보통", RegexOptions.IgnoreCase)) return 1;
        if (Regex.IsMatch(text, @"\b(light|low)\b|낮음|가벼움", RegexOptions.IgnoreCase)) return 0;
        return -1;
    }
    private static int NextGpt(int current, int direction)
    {
        if (current < 0 || current > 4) throw new InvalidOperationException("GPT's current effort is not Light, Medium, High, Extra High, or Ultra.");
        return Math.Max(0, Math.Min(4, current + direction));
    }
    private static string GptLabel(int value) { return new[] { "Light", "Medium", "High", "Extra High", "Ultra" }[value]; }
    private static AutomationElement CodexModelButton(AutomationElement root)
    {
        return CodexModelButton(Elements(root, ControlType.Button));
    }
    private static AutomationElement CodexModelButton(AutomationElement[] buttons)
    {
        // The popup also has a model link. Require effort text to select the composer button.
        const string model = @"(?:GPT[\s-]*)?6\.1[\s-]+Sol\b";
        AutomationElement found = null;
        foreach (AutomationElement button in buttons)
        {
            string description = Name(button);
            if (!Regex.IsMatch(description, model, RegexOptions.IgnoreCase) &&
                (description.Length == 0 || Regex.IsMatch(description, @"model|모델", RegexOptions.IgnoreCase)))
                description = Describe(button);
            if (Regex.IsMatch(description, model, RegexOptions.IgnoreCase) && GptEffort(description) < 0)
                description = Describe(button);
            if (!Regex.IsMatch(description, model, RegexOptions.IgnoreCase) || GptEffort(description) < 0) continue;
            if (found != null) throw new InvalidOperationException("More than one GPT model/effort control; no action taken.");
            found = button;
        }
        return found;
    }
    private static bool IsSupportedGptMode(string name)
    {
        // The mode switch reads "..., current mode: Codex" or "...: ChatGPT"; both show the same slider.
        return Regex.IsMatch(name, @"(mode|모드).*\b(Codex|ChatGPT)\b", RegexOptions.IgnoreCase);
    }
    // Mouse-free attempt, like Claude's: open the popup through UI Automation and set its
    // slider if one is exposed with GPT's five steps. Not yet seen on a live ChatGPT window;
    // anything unexpected closes what it opened and returns false for the image flow.
    private static bool TrySetGptSlider(AutomationElement buttonModel, int current, int target)
    {
        object pattern;
        if (buttonModel == null || !buttonModel.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out pattern))
        {
            trace.AppendLine("GPT model button has no expand pattern; using the image flow.");
            return false;
        }
        ExpandCollapsePattern popup = (ExpandCollapsePattern)pattern;
        AutomationElement before = null;
        try { before = AutomationElement.FocusedElement; if (!IsTextEntry(before)) before = null; } catch { before = null; }
        bool opened = false;
        try
        {
            EnsureForeground();
            popup.Expand();
            opened = true;
            System.Windows.Rect anchor = buttonModel.Current.BoundingRectangle;
            Condition sliderCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Slider);
            RangeValuePattern range = null;
            Stopwatch timer = Stopwatch.StartNew();
            do
            {
                EnsureForeground();
                foreach (AutomationElement candidate in targetRoot.FindAll(TreeScope.Descendants, sliderCondition))
                    try
                    {
                        System.Windows.Rect bounds = candidate.Current.BoundingRectangle;
                        if (!candidate.Current.IsOffscreen && !bounds.IsEmpty && Math.Abs(bounds.Y - anchor.Y) < 400 &&
                            Math.Abs(bounds.X - anchor.X) < 500 && candidate.TryGetCurrentPattern(RangeValuePattern.Pattern, out pattern))
                        { range = (RangeValuePattern)pattern; break; }
                    }
                    catch { }
                if (range != null) break;
                Thread.Sleep(25);
            } while (timer.ElapsedMilliseconds < 700);
            if (range == null) { trace.AppendLine("GPT slider not exposed to UI Automation; using the image flow."); return false; }
            double minimum = range.Current.Minimum, maximum = range.Current.Maximum;
            if (maximum - minimum != 4 || range.Current.IsReadOnly || (int)Math.Round(range.Current.Value - minimum) != current)
            {
                trace.AppendLine("GPT slider range=" + minimum + ".." + maximum + ", value=" + range.Current.Value + "; using the image flow.");
                return false;
            }
            range.SetValue(minimum + target);
            int confirmed = -1;
            timer.Restart();
            do
            {
                Thread.Sleep(25);
                try { confirmed = (int)Math.Round(range.Current.Value - minimum); } catch { }
            } while (confirmed != target && timer.ElapsedMilliseconds < 1000);
            trace.AppendLine("GPT slider set through UI Automation; confirmed=" + confirmed);
            if (confirmed != target) throw new InvalidOperationException("GPT's slider did not confirm " + GptLabel(target) + ".");
            popup.Collapse();
            opened = false;
            int shown = -1;
            timer.Restart();
            do
            {
                shown = CurrentButtonEffort(buttonModel, true);
                if (shown == target) break;
                Thread.Sleep(25);
            } while (timer.ElapsedMilliseconds < 1000);
            trace.AppendLine("GPT model button after close=" + shown);
            if (shown != target) throw new InvalidOperationException("GPT's model button did not confirm " + GptLabel(target) + ".");
            bool restored = false;
            if (before != null)
                restored = RestoreTextFocus(before);
            Record("GPT 6.1 Sol: " + GptLabel(target) + " (slider verified)" + (before == null ? "" : restored ? "; chat input focused" : "; focus not restored"));
            return true;
        }
        // A vanished element or a refused value means the slider cannot be driven this way.
        catch (ElementNotAvailableException error) { trace.AppendLine("GPT slider attempt failed (" + error.Message + "); using the image flow."); return false; }
        catch (ArgumentException error) { trace.AppendLine("GPT slider attempt failed (" + error.Message + "); using the image flow."); return false; }
        finally
        {
            // Leave the popup closed so the image flow opens it with its usual mouse click.
            if (opened)
                try { popup.Collapse(); Thread.Sleep(150); } catch { }
        }
    }
    private static void SwitchCodex(AutomationElement root, int direction)
    {
        EnsureForeground();
        int current;
        bool wasOpen = GptImageSlider.TryReadCurrent(targetWindow, out current);
        AutomationElement buttonModel = null;
        trace.AppendLine("GPT popup initially open=" + wasOpen + ", thumb=" + current);
        // An open popup can hide the composer's controls from accessibility.
        // Its exact GPT-6.1 Sol image identifies the intended model directly.
        if (!wasOpen)
        {
            bool supportedMode = false;
            AutomationElement[] buttons = Elements(root, ControlType.Button);
            foreach (AutomationElement button in buttons)
                if (IsSupportedGptMode(Name(button))) supportedMode = true;
            trace.AppendLine("ChatGPT or Codex mode=" + supportedMode);
            if (!supportedMode) throw new InvalidOperationException("The active ChatGPT window must be in ChatGPT or Codex mode.");
            buttonModel = CodexModelButton(buttons);
        }
        Stopwatch readTimer = Stopwatch.StartNew();
        if (wasOpen)
        {
            while (current < 0 && readTimer.ElapsedMilliseconds < 1400)
            {
                EnsureForeground();
                Thread.Sleep(20);
                if (!GptImageSlider.TryReadCurrent(targetWindow, out current))
                    throw new InvalidOperationException("GPT popup closed while reading its thumb; no click sent.");
            }
            if (current < 0) throw new InvalidOperationException("Cannot identify one GPT slider thumb; no click sent.");
            trace.AppendLine("Current read from already open GPT popup: " + GptLabel(current));
        }
        else
        {
            do
            {
                EnsureForeground();
                if (buttonModel != null) break;
                buttonModel = CodexModelButton(root);
                if (buttonModel != null) break;
                Thread.Sleep(20);
            } while (readTimer.ElapsedMilliseconds < 1400);
            if (buttonModel == null) throw new InvalidOperationException("GPT 6.1 Sol is not selected, or its model control is unavailable.");
            string modelDescription = Name(buttonModel);
            if (GptEffort(modelDescription) < 0) modelDescription = Describe(buttonModel);
            current = GptEffort(modelDescription);
            trace.AppendLine("model=" + modelDescription + ", current=" + current);
        }
        int target = NextGpt(current, direction);
        if (target == current)
        {
            FinishWithInputFocus("GPT 6.1 Sol: " + GptLabel(target) + " (unchanged)");
            return;
        }
        if (!wasOpen && TrySetGptSlider(buttonModel, current, target)) return;
        EnsureForeground();
        if (!wasOpen)
        {
            // Invoke produces a synthetic click and skips the app's pointer-down
            // handler. Open exactly as a mouse click so positioning/focus agree.
            ClickModelButton(buttonModel);
        }
        ClickPoint(delegate
        {
            System.Drawing.Point freshPoint;
            Stopwatch openTimer = Stopwatch.StartNew();
            do
            {
                EnsureForeground();
                if (GptImageSlider.TryLocate(targetWindow, current, target, out freshPoint))
                {
                    trace.AppendLine("StrokesPlus image match succeeded; target=" + GptLabel(target));
                    return freshPoint;
                }
                Thread.Sleep(20);
            } while (openTimer.ElapsedMilliseconds < 1600);
            throw new InvalidOperationException("GPT's slider image was not found; no click sent. Check theme and display scale.");
        }, true);
        // Confirm the changed thumb before dismissing the popup and restoring
        // the composer. Do not inject Escape or force keyboard focus.
        Stopwatch verifyTimer = Stopwatch.StartNew();
        int confirmed = -1;
        do
        {
            EnsureForeground();
            bool popupVisible = GptImageSlider.TryReadCurrent(targetWindow, out confirmed);
            if (!popupVisible)
            {
                // If the app itself dismisses the popup, use the restored composer label.
                confirmed = CurrentButtonEffort(buttonModel, true);
                if (confirmed < 0)
                {
                    buttonModel = CodexModelButton(root);
                    confirmed = CurrentButtonEffort(buttonModel, true);
                }
            }
            if (confirmed == target) break;
            Thread.Sleep(20);
        } while (verifyTimer.ElapsedMilliseconds < 2000);
        trace.AppendLine("confirmed=" + confirmed);
        if (confirmed != target) throw new InvalidOperationException("GPT's slider click did not confirm " + GptLabel(target) + ".");
        FinishWithInputFocus("GPT 6.1 Sol: " + GptLabel(target) + " (image verified)");
    }
    private static bool IsModelControl(string name)
    {
        return Regex.IsMatch(name, @"^\s*(?:Model|모델)\s*:", RegexOptions.IgnoreCase);
    }
    private static bool HasModelControl(AutomationElement[] buttons)
    {
        foreach (AutomationElement button in buttons) if (IsModelControl(Name(button))) return true;
        return false;
    }
    private static AutomationElement FocusedTextEntry()
    {
        try { AutomationElement focused = AutomationElement.FocusedElement; return IsTextEntry(focused) ? focused : null; } catch { return null; }
    }
    private static string GiveFocusBack(AutomationElement typing)
    {
        if (typing == null) return "";
        return RestoreTextFocus(typing) ? "; focus restored" : "; focus not restored";
    }
    // A closing popup hands focus back to its button a moment later, which would undo
    // an immediate SetFocus; keep giving focus back until the text entry holds it.
    private static bool HasFocus(AutomationElement element)
    {
        AutomationElement focused = AutomationElement.FocusedElement;
        return focused != null && Automation.Compare(focused, element);
    }
    private static bool RestoreTextFocus(AutomationElement before)
    {
        Stopwatch timer = Stopwatch.StartNew();
        do
        {
            try
            {
                // The same entry, not just any: the file pane's filter box is a text entry too.
                if (!HasFocus(before)) { focusTouched = true; before.SetFocus(); }
                Thread.Sleep(60);
                if (HasFocus(before)) { trace.AppendLine("Focus restored at " + timer.ElapsedMilliseconds + " ms."); return true; }
            }
            catch { Thread.Sleep(60); }
        } while (timer.ElapsedMilliseconds < 600);
        return false;
    }
    // Mouse-free: the effort popup's slider exposes its 0-5 value to UI Automation.
    // Returns false, leaving any open popup to the image flow, when it is not exposed.
    private static bool TrySetClaudeSlider(AutomationElement root, AutomationElement trigger, int current, int target)
    {
        object pattern;
        if (!trigger.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out pattern)) return false;
        ExpandCollapsePattern popup = (ExpandCollapsePattern)pattern;
        AutomationElement before = null;
        try { before = AutomationElement.FocusedElement; if (!IsTextEntry(before)) before = null; } catch { before = null; }
        EnsureForeground();
        popup.Expand();
        System.Windows.Rect anchor = trigger.Current.BoundingRectangle;
        Condition sliderCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Slider);
        AutomationElement slider = null;
        Stopwatch timer = Stopwatch.StartNew();
        do
        {
            EnsureForeground();
            foreach (AutomationElement candidate in root.FindAll(TreeScope.Descendants, sliderCondition))
                try
                {
                    System.Windows.Rect bounds = candidate.Current.BoundingRectangle;
                    // The popup opens just above its button.
                    if (!candidate.Current.IsOffscreen && !bounds.IsEmpty && Math.Abs(bounds.Bottom - anchor.Top) < 300 &&
                        Math.Abs(bounds.X - anchor.X) < 400 && candidate.TryGetCurrentPattern(RangeValuePattern.Pattern, out pattern))
                    { slider = candidate; break; }
                }
                catch { }
            if (slider != null) break;
            Thread.Sleep(25);
        } while (timer.ElapsedMilliseconds < 1000);
        if (slider == null) { trace.AppendLine("Effort slider not exposed to UI Automation; using the image flow."); return false; }
        RangeValuePattern range = (RangeValuePattern)pattern;
        if (range.Current.Minimum != 0 || range.Current.Maximum != 5 || range.Current.IsReadOnly || (int)Math.Round(range.Current.Value) != current)
        {
            trace.AppendLine("Effort slider range=" + range.Current.Minimum + ".." + range.Current.Maximum + ", value=" + range.Current.Value + "; using the image flow.");
            return false;
        }
        try { range.SetValue(target); }
        catch (Exception error) { trace.AppendLine("Effort slider rejected SetValue (" + error.Message + "); using the image flow."); return false; }
        int confirmed = -1;
        timer.Restart();
        do
        {
            Thread.Sleep(25);
            try { confirmed = (int)Math.Round(range.Current.Value); } catch { }
        } while (confirmed != target && timer.ElapsedMilliseconds < 1000);
        trace.AppendLine("Effort slider set through UI Automation; confirmed=" + confirmed);
        if (confirmed != target) throw new InvalidOperationException("Claude's effort slider did not confirm " + Label(target) + ".");
        popup.Collapse();
        // The button's own name is the app's record of the chosen effort.
        int shown = -1;
        timer.Restart();
        do
        {
            try { shown = ClaudeEffort(trigger.Current.Name ?? ""); } catch { }
            if (shown == target) break;
            Thread.Sleep(25);
        } while (timer.ElapsedMilliseconds < 1000);
        trace.AppendLine("effort button after close=" + shown);
        if (shown != target) throw new InvalidOperationException("Claude's effort button did not confirm " + Label(target) + ".");
        bool restored = false;
        if (before != null)
            restored = RestoreTextFocus(before);
        Record("Opus 5.5: " + Label(target) + " (slider verified)" + (before == null ? "" : restored ? "; chat input focused" : "; focus not restored"));
        return true;
    }
    private static void SwitchClaude(AutomationElement root, int direction)
    {
        // Chromium builds its accessibility tree on request, so the first lookup
        // can return only the window frame. Wait for the composer's model control.
        AutomationElement[] buttons;
        AutomationElement buttonModel;
        Stopwatch treeTimer = Stopwatch.StartNew();
        int lookups = 0;
        do
        {
            EnsureForeground();
            buttons = Elements(root, ControlType.Button);
            lookups++;
            buttonModel = ModelButton(buttons, ClaudeModelPattern);
            if (buttonModel != null || HasModelControl(buttons)) break;
            Thread.Sleep(50);
        } while (treeTimer.ElapsedMilliseconds < 1500);
        if (lookups > 1) trace.AppendLine("Claude controls lookups=" + lookups + ", waited=" + treeTimer.ElapsedMilliseconds + " ms.");
        if (buttonModel == null) throw new InvalidOperationException("Opus 5.5 is not selected, or its model control is unavailable.");
        trace.AppendLine("model=" + Name(buttonModel));
        int triggers = 0;
        AutomationElement trigger = null;
        foreach (AutomationElement button in buttons)
            if (IsEffortTrigger(Name(button))) { triggers++; trigger = button; }
        // Both tabs keep effort in the model button's menu; only the Code tab offers Ultracode.
        if (triggers == 0) { SwitchClaudeChat(root, buttonModel, direction, ClaudeCodeTabSelected(root) ? 5 : 4); return; }
        if (triggers != 1) throw new InvalidOperationException("Claude's effort button is unavailable or ambiguous.");
        int current = ClaudeEffort(Name(trigger));
        trace.AppendLine("effort trigger=" + Name(trigger) + ", current=" + current);
        int target = NextClaude(current, direction);
        if (target == current) { FinishWithInputFocus("Opus 5.5: " + Label(target) + " (unchanged)"); return; }
        if (TrySetClaudeSlider(root, trigger, current, target)) return;

        System.Windows.Rect triggerBounds = trigger.Current.BoundingRectangle;
        if (!triggerBounds.IsEmpty)
            ClaudeImageSlider.ExpectNearButton(targetWindow, new System.Drawing.Rectangle((int)Math.Floor(triggerBounds.X),
                (int)Math.Floor(triggerBounds.Y), (int)Math.Ceiling(triggerBounds.Width), (int)Math.Ceiling(triggerBounds.Height)));
        object expandPattern;
        bool alreadyOpen = trigger.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out expandPattern) &&
            ((ExpandCollapsePattern)expandPattern).Current.ExpandCollapseState == ExpandCollapseState.Expanded;
        alreadyOpen = alreadyOpen || ClaudeImageSlider.IsPopupVisible(targetWindow);
        if (!alreadyOpen) ClickControlButton(trigger, false);

        ClickPoint(delegate
        {
            System.Drawing.Point freshPoint;
            Stopwatch openTimer = Stopwatch.StartNew();
            do
            {
                EnsureForeground();
                if (ClaudeImageSlider.TryLocate(targetWindow, current, target, out freshPoint))
                {
                    trace.AppendLine("StrokesPlus image match succeeded; target=" + Label(target));
                    return freshPoint;
                }
                Thread.Sleep(25);
            } while (openTimer.ElapsedMilliseconds < 1600);
            try { trace.AppendLine("Slider image miss: trigger=" + triggerBounds + ", " + ClaudeImageSlider.DescribeMiss(targetWindow)); } catch { }
            throw new InvalidOperationException("Claude's slider image was not found; no click sent. Check theme and display scale.");
        }, true);
        // Confirm the change before dismissing the popup and restoring the composer.

        Stopwatch timer = Stopwatch.StartNew();
        int confirmed = -1;
        do
        {
            EnsureForeground();
            if (!ClaudeImageSlider.TryReadCurrent(targetWindow, out confirmed))
            {
                confirmed = CurrentButtonEffort(trigger, false);
                if (confirmed < 0)
                {
                    trigger = ClaudeEffortTrigger(root);
                    confirmed = CurrentButtonEffort(trigger, false);
                }
            }
            if (confirmed == target) break;
            Thread.Sleep(25);
        } while (timer.ElapsedMilliseconds < 2000);
        trace.AppendLine("confirmed=" + confirmed);
        if (confirmed != target) throw new InvalidOperationException("Claude's slider click did not confirm " + Label(target) + ".");
        FinishWithInputFocus("Opus 5.5: " + Label(target) + " (image verified)");
    }
    private sealed class ClaudeMenuItem
    {
        internal AutomationElement Element;
        internal string Name;
        internal System.Windows.Rect Menu;
    }
    private static readonly Condition claudeMenuItemCondition = new OrCondition(
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.RadioButton),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
    private static bool claudeMenuFlow;
    private static System.Drawing.Rectangle claudeMenuBounds = System.Drawing.Rectangle.Empty;
    private static bool ClaudeCodeTabSelected(AutomationElement root)
    {
        foreach (AutomationElement tab in Elements(root, ControlType.RadioButton))
        {
            object pattern;
            try
            {
                if (Regex.IsMatch(Name(tab), @"^\s*Code\b", RegexOptions.IgnoreCase) &&
                    tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out pattern) &&
                    ((SelectionItemPattern)pattern).Current.IsSelected) return true;
            }
            catch { }
        }
        return false;
    }
    // Low, Medium, High, Extra, Max, and Ultracode, which only the Code tab's menu lists.
    private static int ChatEffortOption(string name)
    {
        string[] patterns = {
            @"^\s*(low\b|낮음)", @"^\s*(medium\b|중간|보통)", @"^\s*(high\b|높음)",
            @"^\s*(extra\b|xhigh\b|엑스트라|매우\s*높음|아주\s*높음)", @"^\s*(max\b|maximum\b|최대)",
            @"^\s*(ultra[\s-]*code\b|울트라[\s-]*코드)" };
        for (int level = 0; level < patterns.Length; level++)
            if (Regex.IsMatch(name, patterns[level], RegexOptions.IgnoreCase)) return level;
        return -1;
    }
    // top is 5 in the Code tab (up to Ultracode) and 4 in the Chat tab (up to Max).
    private static int NextClaudeMenu(int current, int direction, int top)
    {
        if (current < 0 || current > top)
            throw new InvalidOperationException("Claude's effort is not one of Low, Medium, High, Extra, Max" + (top == 5 ? ", or Ultracode." : "."));
        return Math.Max(0, Math.Min(top, current + direction));
    }
    // Only items inside menu popups are read, so conversation text is never matched.
    private static List<ClaudeMenuItem> ClaudeMenuItems(AutomationElement root)
    {
        List<ClaudeMenuItem> items = new List<ClaudeMenuItem>();
        CacheRequest cache = new CacheRequest();
        cache.Add(AutomationElement.NameProperty);
        cache.Add(AutomationElement.IsOffscreenProperty);
        cache.Add(AutomationElement.IsEnabledProperty);
        cache.Add(AutomationElement.BoundingRectangleProperty);
        cache.TreeScope = TreeScope.Element;
        using (cache.Activate())
            foreach (AutomationElement menu in root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Menu)))
                try
                {
                    if (menu.Cached.IsOffscreen) continue;
                    foreach (AutomationElement item in menu.FindAll(TreeScope.Descendants, claudeMenuItemCondition))
                        if (!item.Cached.IsOffscreen && item.Cached.IsEnabled)
                            items.Add(new ClaudeMenuItem { Element = item, Name = item.Cached.Name ?? "", Menu = menu.Cached.BoundingRectangle });
                }
                catch { }
        return items;
    }
    private static ClaudeMenuItem ClaudeEffortRow(List<ClaudeMenuItem> items)
    {
        foreach (ClaudeMenuItem item in items) if (IsEffortTrigger(item.Name)) return item;
        return null;
    }
    private static ClaudeMenuItem ClaudeEffortOption(List<ClaudeMenuItem> items, int level)
    {
        foreach (ClaudeMenuItem item in items) if (ChatEffortOption(item.Name) == level) return item;
        return null;
    }
    private static bool IsChosen(AutomationElement item)
    {
        object pattern;
        try
        {
            if (item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out pattern) &&
                ((SelectionItemPattern)pattern).Current.IsSelected) return true;
            if (item.TryGetCurrentPattern(TogglePattern.Pattern, out pattern) &&
                ((TogglePattern)pattern).Current.ToggleState == ToggleState.On) return true;
        }
        catch { }
        return false;
    }
    private static int ChosenChatEffort(List<ClaudeMenuItem> items)
    {
        int found = -1;
        foreach (ClaudeMenuItem item in items)
        {
            int level = ChatEffortOption(item.Name);
            if (level < 0 || level == found || !IsChosen(item.Element)) continue;
            if (found >= 0) return -1;
            found = level;
        }
        return found;
    }
    private static string MenuNames(List<ClaudeMenuItem> items)
    {
        // Menu labels only, shortened; used when a step cannot find its item.
        StringBuilder names = new StringBuilder();
        for (int i = 0; i < items.Count && i < 20; i++)
            names.Append(items[i].Name.Length > 24 ? items[i].Name.Substring(0, 24) : items[i].Name).Append(" | ");
        return names.ToString();
    }
    private static System.Drawing.Point MenuClickPoint(ClaudeMenuItem item)
    {
        return ElementClickPoint(item.Element, "Claude's menu item");
    }
    private static System.Drawing.Point ElementClickPoint(AutomationElement element, string what)
    {
        EnsureForeground();
        System.Windows.Rect bounds = element.Current.BoundingRectangle;
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0 ||
            Double.IsNaN(bounds.X) || Double.IsNaN(bounds.Y) || Double.IsInfinity(bounds.X) || Double.IsInfinity(bounds.Y))
            throw new InvalidOperationException(what + " has no valid position; no click sent.");
        System.Windows.Point center = new System.Windows.Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        if (targetRoot == null || !targetRoot.Current.BoundingRectangle.Contains(center))
            throw new InvalidOperationException(what + " is outside the active window; no click sent.");
        return new System.Drawing.Point((int)Math.Round(center.X), (int)Math.Round(center.Y));
    }
    // Mouse-free: the header's View options menu has a Background tasks check box that
    // shows or hides the tasks pane; it is toggled through UI Automation, with no keys.
    private static void ToggleTasksPane(AutomationElement root)
    {
        AutomationElement typing = FocusedTextEntry();
        // An open pane is closed with its own Close button; the menu is only needed to open it.
        AutomationElement pane = TasksPane(root);
        if (pane != null) { CloseTasksPane(root, pane, typing); return; }
        AutomationElement options = null;
        foreach (AutomationElement button in Elements(root, ControlType.Button))
            if (Regex.IsMatch(Name(button), @"^\s*(보기\s*옵션|view\s*options)\s*$", RegexOptions.IgnoreCase)) { options = button; break; }
        object pattern;
        if (options == null || !options.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out pattern))
            throw new InvalidOperationException("The View options button was not found; open a session in the Code tab.");
        ExpandCollapsePattern menu = (ExpandCollapsePattern)pattern;
        EnsureForeground();
        // The menu takes the window's focus; the IME is renewed after the run.
        focusTouched = true;
        menu.Expand();
        AutomationElement box = null;
        List<ClaudeMenuItem> items = null;
        Stopwatch timer = Stopwatch.StartNew();
        do
        {
            EnsureForeground();
            items = ClaudeMenuItems(root);
            foreach (ClaudeMenuItem item in items)
                if (IsTasksLabel(item.Name)) { box = item.Element; break; }
            if (box != null) break;
            Thread.Sleep(25);
        } while (timer.ElapsedMilliseconds < 1600);
        if (box == null || !box.TryGetCurrentPattern(TogglePattern.Pattern, out pattern))
        {
            trace.AppendLine("menu items=" + MenuNames(items));
            CloseMenu(root, options);
            throw new InvalidOperationException("Background tasks was not found in the View options menu; no change made.");
        }
        TogglePattern toggle = (TogglePattern)pattern;
        bool show = toggle.Current.ToggleState != ToggleState.On;
        toggle.Toggle();
        // Choosing usually closes the menu, so the pane itself confirms the change.
        timer.Restart();
        bool shown = !show;
        do
        {
            Thread.Sleep(25);
            EnsureForeground();
            shown = TasksPaneShown(root);
        } while (shown != show && timer.ElapsedMilliseconds < 1500);
        CloseMenu(root, options);
        trace.AppendLine("Background tasks: " + (show ? "show" : "hide") + ", pane shown=" + shown);
        if (shown != show) throw new InvalidOperationException("The background tasks pane did not " + (show ? "open." : "close."));
        Record("Tasks: pane " + (show ? "shown" : "hidden") + " (pane verified)" + GiveFocusBack(typing));
    }
    // The menu label carries the running task count, e.g. "백그라운드 작업 4".
    private static bool IsTasksLabel(string name)
    {
        return Regex.IsMatch(name, @"^\s*(백그라운드\s*작업|background\s*tasks?)(\s*\d+)?\s*$", RegexOptions.IgnoreCase);
    }
    private static bool TasksPaneShown(AutomationElement root) { return TasksPane(root) != null; }
    private static AutomationElement TasksPane(AutomationElement root)
    {
        foreach (AutomationElement group in Elements(root, ControlType.Group))
            if (IsTasksLabel(Name(group))) return group;
        return null;
    }
    private static void CloseTasksPane(AutomationElement root, AutomationElement pane, AutomationElement typing)
    {
        AutomationElement close = null;
        foreach (AutomationElement button in Elements(pane, ControlType.Button))
            if (Regex.IsMatch(Name(button), @"^\s*(닫기|close)\s*$", RegexOptions.IgnoreCase)) { close = button; break; }
        object invoke;
        if (close == null || !close.TryGetCurrentPattern(InvokePattern.Pattern, out invoke))
            throw new InvalidOperationException("The background tasks pane has no Close button; no change made.");
        EnsureForeground();
        focusTouched = true;
        ((InvokePattern)invoke).Invoke();
        Stopwatch timer = Stopwatch.StartNew();
        bool shown = true;
        do
        {
            Thread.Sleep(25);
            EnsureForeground();
            shown = TasksPaneShown(root);
        } while (shown && timer.ElapsedMilliseconds < 1500);
        if (shown) throw new InvalidOperationException("The background tasks pane did not close.");
        Record("Tasks: pane hidden (pane verified)" + GiveFocusBack(typing));
    }
    // Puts the caret in the chat input with one real click, so typing can start at once.
    private static void FocusInput(AutomationElement root)
    {
        // Chromium may not have built its accessibility tree yet; look again for a short while.
        Stopwatch timer = Stopwatch.StartNew();
        do
        {
            EnsureForeground();
            RememberChatInput(root);
            if (!chatInputBounds.IsEmpty) break;
            Thread.Sleep(50);
        } while (timer.ElapsedMilliseconds < 1500);
        if (chatInputBounds.IsEmpty) throw new InvalidOperationException("The chat input was not found; no click sent.");
        if (ChatInputFocused()) { Record("Input: chat input already focused"); return; }
        System.Windows.Rect window = chatInputWindowBounds;
        ClickPoint(delegate
        {
            System.Windows.Rect currentWindow = targetRoot.Current.BoundingRectangle;
            if (Math.Abs(currentWindow.Width - window.Width) > 1 || Math.Abs(currentWindow.Height - window.Height) > 1)
                throw new InvalidOperationException("Window resized before the input click; no click sent.");
            System.Windows.Rect input = chatInputBounds;
            input.Offset(currentWindow.X - window.X, currentWindow.Y - window.Y);
            System.Drawing.Point point;
            if (!TryInputClickPoint(input, System.Drawing.Rectangle.Empty, out point))
                throw new InvalidOperationException("The chat input is too small to click; no click sent.");
            return point;
        });
        trace.AppendLine("Chat input clicked at " + operationTimer.ElapsedMilliseconds + " ms.");
        bool focused = false;
        timer.Restart();
        do
        {
            EnsureForeground();
            try { focused = ChatInputFocused(); } catch { }
            if (focused) break;
            Thread.Sleep(10);
        } while (timer.ElapsedMilliseconds < 500);
        trace.AppendLine("Chat input focus confirmed=" + focused);
        Record("Input: " + (focused ? "chat input focused" : "chat input clicked; focus unconfirmed"));
    }
    private static void AddMenuBounds(ClaudeMenuItem item)
    {
        if (item == null || item.Menu.IsEmpty) return;
        System.Drawing.Rectangle menu = new System.Drawing.Rectangle((int)Math.Floor(item.Menu.X), (int)Math.Floor(item.Menu.Y),
            (int)Math.Ceiling(item.Menu.Width), (int)Math.Ceiling(item.Menu.Height));
        claudeMenuBounds = claudeMenuBounds.IsEmpty ? menu : System.Drawing.Rectangle.Union(claudeMenuBounds, menu);
    }
    // Mouse-free version of the Chat tab flow below; false hands over to it with the menu closed.
    private static bool TrySwitchClaudeChatMenu(AutomationElement root, AutomationElement buttonModel, int current, int direction, int top)
    {
        AutomationElement before = null;
        try { before = AutomationElement.FocusedElement; if (!IsTextEntry(before)) before = null; } catch { before = null; }
        ClaudeMenuItem row;
        List<ClaudeMenuItem> items = OpenMenuRow(root, buttonModel, ClaudeEffortRow,
            delegate(List<ClaudeMenuItem> open) { return ClaudeEffortOption(open, 0) != null || ClaudeEffortOption(open, 4) != null; }, out row);
        if (items == null)
        {
            CloseMenu(root, buttonModel);
            trace.AppendLine("Effort submenu did not open through UI Automation; using mouse clicks.");
            Thread.Sleep(150);
            return false;
        }
        int chosen = ChosenChatEffort(items);
        if (chosen >= 0) current = chosen;
        if (current < 0) current = ClaudeEffort(row.Name);
        int target = NextClaudeMenu(current, direction, top);
        trace.AppendLine("current=" + current + ", target=" + target);
        bool restored = false;
        if (target != current)
        {
            ClaudeMenuItem option = ClaudeEffortOption(items, target);
            if (option == null || !InvokeMenuItem(option))
            {
                CloseMenu(root, buttonModel);
                throw new InvalidOperationException("Claude's " + Label(target) + " option cannot be chosen; no change made.");
            }
            // Confirm from the menu's own state, or from the model button once the menu has closed.
            int confirmed = -1;
            Stopwatch timer = Stopwatch.StartNew();
            do
            {
                EnsureForeground();
                items = ClaudeMenuItems(root);
                confirmed = ChosenChatEffort(items);
                ClaudeMenuItem live = ClaudeEffortRow(items);
                if (confirmed != target && live != null && ClaudeEffort(live.Name) >= 0) confirmed = ClaudeEffort(live.Name);
                if (confirmed != target && items.Count == 0)
                {
                    string name = "";
                    try { name = buttonModel.Current.Name ?? ""; } catch { }
                    if (Regex.IsMatch(name, ClaudeModelPattern, RegexOptions.IgnoreCase) && ClaudeEffort(name) >= 0) confirmed = ClaudeEffort(name);
                }
                if (confirmed == target) break;
                Thread.Sleep(25);
            } while (timer.ElapsedMilliseconds < 2000);
            trace.AppendLine("confirmed=" + confirmed);
            if (confirmed != target)
            {
                CloseMenu(root, buttonModel);
                throw new InvalidOperationException("Claude's menu did not confirm " + Label(target) + ".");
            }
        }
        CloseMenu(root, buttonModel);
        if (before != null) restored = RestoreTextFocus(before);
        Record("Opus 5.5: " + Label(target) + (target == current ? " (unchanged)" : " (menu verified)") +
            (before == null ? "" : restored ? "; chat input focused" : "; focus not restored"));
        return true;
    }
    private static void SwitchClaudeChat(AutomationElement root, AutomationElement buttonModel, int direction, int top)
    {
        claudeMenuFlow = true;
        int current = ClaudeEffort(Name(buttonModel));
        trace.AppendLine((top == 5 ? "Code" : "Chat") + " tab; effort on model button=" + current);
        if (current >= 0 && current <= top && NextClaudeMenu(current, direction, top) == current)
        {
            FinishWithInputFocus("Opus 5.5: " + Label(current) + " (unchanged)");
            return;
        }
        if (TrySwitchClaudeChatMenu(root, buttonModel, current, direction, top)) return;
        object expandPattern;
        bool alreadyOpen = buttonModel.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out expandPattern) &&
            ((ExpandCollapsePattern)expandPattern).Current.ExpandCollapseState == ExpandCollapseState.Expanded;
        System.Drawing.Point startCursor, lastPoint = System.Drawing.Point.Empty, now;
        bool haveCursor = GetCursorPos(out startCursor);
        if (!alreadyOpen) ClickControlButton(buttonModel, false);
        int target;
        try
        {
            List<ClaudeMenuItem> items = null;
            ClaudeMenuItem row = null, option = null;
            Stopwatch timer = Stopwatch.StartNew();
            do
            {
                EnsureForeground();
                items = ClaudeMenuItems(root);
                row = ClaudeEffortRow(items);
                if (row != null) break;
                Thread.Sleep(25);
            } while (timer.ElapsedMilliseconds < 1600);
            if (row == null)
            {
                trace.AppendLine("menu items=" + MenuNames(items));
                throw new InvalidOperationException("Claude's effort row was not found in the model menu; no change made.");
            }
            trace.AppendLine("effort row=" + row.Name);
            AddMenuBounds(row);
            if (current < 0) current = ClaudeEffort(row.Name);
            // Leave the cursor on the row so the submenu it opens is not dismissed by hover logic.
            if (ClaudeEffortOption(items, 0) == null && ClaudeEffortOption(items, 4) == null)
                ClickPoint(delegate { return lastPoint = MenuClickPoint(row); }, true, false);
            timer.Restart();
            do
            {
                EnsureForeground();
                items = ClaudeMenuItems(root);
                if (current < 0) current = ChosenChatEffort(items);
                if (current >= 0 && current <= top) option = ClaudeEffortOption(items, NextClaudeMenu(current, direction, top));
                if (option != null) break;
                Thread.Sleep(25);
            } while (timer.ElapsedMilliseconds < 1600);
            if (option == null)
            {
                trace.AppendLine("current=" + current + "; menu items=" + MenuNames(items));
                throw new InvalidOperationException("Claude's effort options were not found in the menu; no change made.");
            }
            target = NextClaudeMenu(current, direction, top);
            trace.AppendLine("current=" + current + ", target option=" + option.Name);
            AddMenuBounds(option);
            if (target != current)
            {
                ClickPoint(delegate { return lastPoint = MenuClickPoint(option); }, true, false);
                // Confirm from the menu's own state, or from the model button once the menu has closed.
                int confirmed = -1;
                timer.Restart();
                do
                {
                    EnsureForeground();
                    items = ClaudeMenuItems(root);
                    confirmed = ChosenChatEffort(items);
                    row = ClaudeEffortRow(items);
                    if (confirmed != target && row != null && ClaudeEffort(row.Name) >= 0) confirmed = ClaudeEffort(row.Name);
                    if (confirmed != target)
                    {
                        string live = "";
                        try { live = buttonModel.Current.Name ?? ""; } catch { }
                        if (Regex.IsMatch(live, ClaudeModelPattern, RegexOptions.IgnoreCase) && ClaudeEffort(live) >= 0)
                            confirmed = ClaudeEffort(live);
                    }
                    if (confirmed == target) break;
                    Thread.Sleep(25);
                } while (timer.ElapsedMilliseconds < 2000);
                trace.AppendLine("confirmed=" + confirmed);
                if (confirmed != target) throw new InvalidOperationException("Claude's menu click did not confirm " + Label(target) + ".");
            }
        }
        finally
        {
            if (haveCursor && !lastPoint.IsEmpty && GetForegroundWindow() == targetWindow && GetCursorPos(out now) && now == lastPoint)
                SetCursorPos(startCursor.X, startCursor.Y);
        }
        FinishWithInputFocus("Opus 5.5: " + Label(target) + (target == current ? " (unchanged)" : " (menu verified)"));
    }
    // Sessions page (Group and sort > View all): an Active / Archived tab strip.
    private static bool IsArchivedTab(string name)
    {
        return Regex.IsMatch(name, @"^\s*(보관됨|archived)\s*$", RegexOptions.IgnoreCase);
    }
    private static AutomationElement ArchivedTab(AutomationElement root)
    {
        foreach (AutomationElement tab in Elements(root, ControlType.TabItem)) if (IsArchivedTab(Name(tab))) return tab;
        return null;
    }
    // File pane: the Markdown view button is named after the view it switches to.
    private static bool IsMarkdownViewToggle(string name)
    {
        return Regex.IsMatch(name, @"^\s*(소스\s*보기|미리\s*보기|view\s+source|show\s+source|preview)\s*$", RegexOptions.IgnoreCase);
    }
    private static void ToggleMarkdownView(AutomationElement root)
    {
        AutomationElement toggle = null;
        int found = 0;
        foreach (AutomationElement button in Elements(root, ControlType.Button))
            if (IsMarkdownViewToggle(Name(button))) { if (toggle == null) toggle = button; found++; }
        trace.AppendLine("Markdown view buttons=" + found);
        AutomationElement typing = FocusedTextEntry();
        if (toggle == null) { ToggleFilePane(root, typing); return; }
        string before = Name(toggle);
        bool toSource = Regex.IsMatch(before, @"소스|source", RegexOptions.IgnoreCase);
        object invoke;
        if (!toggle.TryGetCurrentPattern(InvokePattern.Pattern, out invoke))
            throw new InvalidOperationException("The Markdown view button cannot be pressed.");
        EnsureForeground();
        // Invoke moves the window's focus to the button; the IME is renewed after the run.
        focusTouched = true;
        ((InvokePattern)invoke).Invoke();
        string after = before;
        Stopwatch timer = Stopwatch.StartNew();
        do
        {
            Thread.Sleep(25);
            try { after = toggle.Current.Name ?? ""; } catch { after = ""; }
            if (after != before && IsMarkdownViewToggle(after)) break;
        } while (timer.ElapsedMilliseconds < 1500);
        trace.AppendLine("Markdown button: " + before + " -> " + after);
        if (after == before || !IsMarkdownViewToggle(after))
            throw new InvalidOperationException("The Markdown view did not confirm the switch.");
        Record("Markdown: " + (toSource ? "source" : "preview") + " (button verified)" + GiveFocusBack(typing));
    }
    // With no Markdown view on screen, the header's Files toggle opens the file pane,
    // or closes it when it is already open without one.
    private static void ToggleFilePane(AutomationElement root, AutomationElement typing)
    {
        AutomationElement files = null;
        foreach (AutomationElement button in Elements(root, ControlType.Button))
            if (Regex.IsMatch(Name(button), @"^\s*(파일|files?)\s*$", RegexOptions.IgnoreCase)) { files = button; break; }
        object pattern;
        if (files == null || !files.TryGetCurrentPattern(TogglePattern.Pattern, out pattern))
            throw new InvalidOperationException("No Markdown view and no Files button is visible; open the Code tab.");
        TogglePattern toggle = (TogglePattern)pattern;
        ToggleState target = toggle.Current.ToggleState == ToggleState.On ? ToggleState.Off : ToggleState.On;
        EnsureForeground();
        focusTouched = true;
        toggle.Toggle();
        Stopwatch timer = Stopwatch.StartNew();
        while (toggle.Current.ToggleState != target && timer.ElapsedMilliseconds < 1500) Thread.Sleep(25);
        string action = target == ToggleState.On ? "open" : "close";
        if (toggle.Current.ToggleState != target) throw new InvalidOperationException("The file pane did not " + action + ".");
        Record("Markdown: file pane " + action + (target == ToggleState.On ? "ed" : "d") + " (button verified)" + GiveFocusBack(typing));
    }
    // Mouse-free: expands the trigger's menu through UI Automation, then opens the row's
    // submenu with one Right arrow key, the only step submenus ignore UI Automation for.
    // A menu opened a moment ago can still move focus or close, so the step runs once
    // more. Returns the items once submenuOpen accepts them, or null.
    private static List<ClaudeMenuItem> OpenMenuRow(AutomationElement root, AutomationElement trigger,
        Func<List<ClaudeMenuItem>, ClaudeMenuItem> findRow, Func<List<ClaudeMenuItem>, bool> submenuOpen, out ClaudeMenuItem row)
    {
        row = null;
        List<ClaudeMenuItem> items = null;
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            // The button's expanded state can outlive its menu, so look for the menu itself.
            items = ClaudeMenuItems(root);
            if (findRow(items) == null)
            {
                object expand;
                if (!trigger.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out expand)) return null;
                EnsureForeground();
                ((ExpandCollapsePattern)expand).Expand();
            }
            Stopwatch timer = Stopwatch.StartNew();
            do
            {
                EnsureForeground();
                items = ClaudeMenuItems(root);
                row = findRow(items);
                if (row != null) break;
                Thread.Sleep(25);
            } while (timer.ElapsedMilliseconds < 1600);
            if (row == null) { trace.AppendLine("menu items=" + MenuNames(items)); return null; }
            trace.AppendLine("attempt " + attempt + ": row=" + row.Name);
            // A menu that just opened moves focus into itself a moment later, which can undo
            // SetFocus; keep focusing the row, and send the key only while the row has focus.
            bool rowFocused = false;
            Stopwatch focusTimer = Stopwatch.StartNew();
            do
            {
                try
                {
                    focusTouched = true;
                    row.Element.SetFocus();
                    Thread.Sleep(40);
                    AutomationElement focused = AutomationElement.FocusedElement;
                    rowFocused = focused != null && (focused.Current.Name ?? "") == row.Name;
                }
                catch { Thread.Sleep(40); }
            } while (!rowFocused && focusTimer.ElapsedMilliseconds < 500);
            trace.AppendLine("row focused=" + rowFocused + " at " + focusTimer.ElapsedMilliseconds + " ms");
            if (!rowFocused) continue;
            // Ctrl+Alt+Right is Claude's "next sidebar tab", so the chord must be fully released.
            WaitForInputRelease();
            EnsureForeground();
            keybd_event(0x27, 0, 1, TypeAhead.OwnKey);
            keybd_event(0x27, 0, 3, TypeAhead.OwnKey);
            timer.Restart();
            do
            {
                EnsureForeground();
                items = ClaudeMenuItems(root);
                if (submenuOpen(items)) return items;
                if (items.Count == 0) break;
                Thread.Sleep(25);
            } while (timer.ElapsedMilliseconds < 1000);
            trace.AppendLine("submenu not open; menu items=" + MenuNames(items));
        }
        return null;
    }
    private static bool InvokeMenuItem(ClaudeMenuItem item)
    {
        object pattern;
        EnsureForeground();
        if (item.Element.TryGetCurrentPattern(InvokePattern.Pattern, out pattern)) { ((InvokePattern)pattern).Invoke(); return true; }
        if (item.Element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out pattern)) { ((SelectionItemPattern)pattern).Select(); return true; }
        return false;
    }
    private static void CloseMenu(AutomationElement root, AutomationElement trigger)
    {
        object pattern;
        try
        {
            if (ClaudeMenuItems(root).Count > 0 && trigger.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out pattern))
                ((ExpandCollapsePattern)pattern).Collapse();
        }
        catch { }
    }
    // Mouse-free: the sidebar's Group and sort menu opens the sessions page through
    // View all, and its Archived tab is selected through UI Automation. No keys are sent.
    private static void OpenArchivedSessions(AutomationElement root)
    {
        AutomationElement archived = ArchivedTab(root);
        if (archived == null)
        {
            // Claude's element tree can still be filling in when the hotkey lands (one run saw
            // 3 buttons where the next saw 120), so the button gets the same wait as the menu.
            AutomationElement menuButton = null;
            Stopwatch buttonTimer = Stopwatch.StartNew();
            int lookups = 0;
            do
            {
                if (lookups++ > 0) Thread.Sleep(100);
                foreach (AutomationElement button in Elements(root, ControlType.Button))
                    if (Regex.IsMatch(Name(button), @"^\s*(그룹화\s*및\s*정렬|group\s*(and|&)\s*sort)\s*$", RegexOptions.IgnoreCase)) { menuButton = button; break; }
            } while (menuButton == null && buttonTimer.ElapsedMilliseconds < 2000);
            trace.AppendLine("Group and sort button " + (menuButton == null ? "missing" : "found") + " after " + lookups + " lookups, " + buttonTimer.ElapsedMilliseconds + " ms.");
            if (menuButton == null) throw new InvalidOperationException("The sidebar's Group and sort button was not found; open the Code tab sidebar.");
            object expand;
            if (!menuButton.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out expand))
                throw new InvalidOperationException("The Group and sort menu cannot be opened.");
            EnsureForeground();
            // The menu takes the window's focus; the IME is renewed after the run.
            focusTouched = true;
            ((ExpandCollapsePattern)expand).Expand();
            ClaudeMenuItem viewAll = null;
            Stopwatch menuTimer = Stopwatch.StartNew();
            do
            {
                EnsureForeground();
                foreach (ClaudeMenuItem item in ClaudeMenuItems(root))
                    if (Regex.IsMatch(item.Name, @"^\s*(모두\s*보기|view\s*all|see\s*all)\s*$", RegexOptions.IgnoreCase)) { viewAll = item; break; }
                if (viewAll != null) break;
                Thread.Sleep(25);
            } while (menuTimer.ElapsedMilliseconds < 1600);
            if (viewAll == null || !InvokeMenuItem(viewAll))
            {
                CloseMenu(root, menuButton);
                throw new InvalidOperationException("View all was not found in the Group and sort menu; no change made.");
            }
            trace.AppendLine("View all invoked at " + operationTimer.ElapsedMilliseconds + " ms.");
            Stopwatch pageTimer = Stopwatch.StartNew();
            do
            {
                EnsureForeground();
                archived = ArchivedTab(root);
                if (archived != null) break;
                Thread.Sleep(50);
            } while (pageTimer.ElapsedMilliseconds < 2000);
            if (archived == null) throw new InvalidOperationException("The sessions page did not show its Archived tab.");
        }
        bool already = IsChosen(archived);
        if (!already)
        {
            object select;
            if (!archived.TryGetCurrentPattern(SelectionItemPattern.Pattern, out select))
                throw new InvalidOperationException("The Archived tab cannot be selected.");
            EnsureForeground();
            focusTouched = true;
            ((SelectionItemPattern)select).Select();
            Stopwatch timer = Stopwatch.StartNew();
            while (!IsChosen(archived) && timer.ElapsedMilliseconds < 1500) Thread.Sleep(25);
            if (!IsChosen(archived)) throw new InvalidOperationException("The Archived tab did not confirm the selection.");
        }
        Record("Sessions: Archived" + (already ? " (already open)" : " (verified)"));
    }
    private static int CurrentButtonEffort(AutomationElement button, bool gpt)
    {
        try
        {
            if (button == null || button.Current.IsOffscreen || !button.Current.IsEnabled) return -1;
            // Read the live label, never the snapshot cached before the click.
            string name = button.Current.Name ?? "";
            if (gpt)
                return Regex.IsMatch(name, @"(?:GPT[\s-]*)?6\.1[\s-]+Sol\b", RegexOptions.IgnoreCase) ? GptEffort(name) : -1;
            return IsEffortTrigger(name) ? ClaudeEffort(name) : -1;
        }
        catch { return -1; }
    }
    private static bool IsEffortTrigger(string name)
    {
        return Regex.IsMatch(name, @"^(Effort|추론\s*강도|사고\s*강도|노력)(\b|\s|:|$)", RegexOptions.IgnoreCase);
    }
    private static AutomationElement ClaudeEffortTrigger(AutomationElement root)
    {
        return ClaudeEffortTrigger(Elements(root, ControlType.Button));
    }
    private static AutomationElement ClaudeEffortTrigger(AutomationElement[] buttons)
    {
        AutomationElement found = null;
        foreach (AutomationElement button in buttons)
        {
            string name = Name(button);
            if (!IsEffortTrigger(name)) continue;
            if (found != null) return null;
            found = button;
        }
        return found;
    }
    private static void ClickModelButton(AutomationElement button)
    {
        ClickControlButton(button, true);
    }
    private static void ClickControlButton(AutomationElement button, bool gpt)
    {
        ClickPoint(delegate
        {
            EnsureForeground();
            if (button == null || button.Current.IsOffscreen || !button.Current.IsEnabled)
                throw new InvalidOperationException("Reasoning button is unavailable; no click sent.");
            System.Windows.Rect bounds = button.Current.BoundingRectangle;
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0 ||
                Double.IsNaN(bounds.X) || Double.IsNaN(bounds.Y) ||
                Double.IsInfinity(bounds.X) || Double.IsInfinity(bounds.Y))
                throw new InvalidOperationException("Reasoning button has no valid position; no click sent.");
            System.Windows.Point center = new System.Windows.Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
            if (targetRoot == null || !targetRoot.Current.BoundingRectangle.Contains(center))
                throw new InvalidOperationException("Reasoning button is outside the active window; no click sent.");
            trace.AppendLine("Opening " + (gpt ? "GPT model" : "Claude effort") + " popup with a mouse click.");
            System.Drawing.Rectangle buttonBounds = new System.Drawing.Rectangle((int)Math.Floor(bounds.X),
                (int)Math.Floor(bounds.Y), (int)Math.Ceiling(bounds.Width), (int)Math.Ceiling(bounds.Height));
            if (gpt) GptImageSlider.ExpectNearButton(targetWindow, buttonBounds);
            else ClaudeImageSlider.ExpectNearButton(targetWindow, buttonBounds);
            return new System.Drawing.Point((int)Math.Round(center.X), (int)Math.Round(center.Y));
        });
    }
    private static void ClickPoint(System.Drawing.Point point)
    {
        ClickPoint(delegate { return point; });
    }
    private static void ClickPoint(Func<System.Drawing.Point> locate, bool protectTextEntry = false, bool restoreCursor = true)
    {
        System.Drawing.Point point;
        Stopwatch locateTimer = Stopwatch.StartNew();
        do
        {
            WaitForInputRelease();
            point = locate();
            EnsureForeground();
            if (protectTextEntry)
            {
                // GPT can retain accessibility focus on the composer while its
                // popover is visible. Only a dismissed popup makes that focus
                // evidence that the user has returned to typing.
                if (TextEntryFocused() && !(app == "chatgpt" && GptImageSlider.IsPopupVisible(targetWindow)))
                    throw new OperationCanceledException("Text entry regained focus; no slider click sent.");
            }
            if (!InputHeld()) break;
            if (locateTimer.ElapsedMilliseconds >= 4000)
                throw new InvalidOperationException("Release Ctrl, Alt and the arrow key, then press the hotkey again.");
        } while (true);
        System.Drawing.Point previous, after;
        bool restore = GetCursorPos(out previous) && restoreCursor;
        if (!SetCursorPos(point.X, point.Y)) throw new InvalidOperationException("Cannot position the cursor on the target control.");
        try
        {
            EnsureForeground();
            mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
            mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
        }
        finally
        {
            if (restore && GetForegroundWindow() == targetWindow && GetCursorPos(out after) && after == point)
                SetCursorPos(previous.X, previous.Y);
        }
    }
    private static string Label(int value) { return new[] { "Low", "Medium", "High", "Extra", "Max", "Ultracode" }[value]; }
    private static bool TextEntryFocused()
    {
        AutomationElement focused = AutomationElement.FocusedElement;
        return IsTextEntry(focused);
    }
    private static bool IsTextEntry(AutomationElement element)
    {
        if (element == null) return false;
        if (element.Current.ControlType == ControlType.Edit) return true;
        if (!element.Current.IsKeyboardFocusable) return false;
        string metadata = element.Current.AutomationId + " " + element.Current.ClassName;
        if (Regex.IsMatch(metadata, @"prompt-textarea|chat[-_]?input|composer|prosemirror", RegexOptions.IgnoreCase)) return true;
        if (chatInputBounds.IsEmpty || targetRoot == null) return false;
        // Claude's effort popup opens over a tall composer, so its slider thumb
        // lies inside the input's bounds without being a text entry.
        ControlType type = element.Current.ControlType;
        if (type != ControlType.Document && type != ControlType.Custom && type != ControlType.Pane) return false;
        System.Windows.Rect window = targetRoot.Current.BoundingRectangle;
        System.Windows.Rect expected = chatInputBounds;
        expected.Offset(window.X - chatInputWindowBounds.X, window.Y - chatInputWindowBounds.Y);
        System.Windows.Rect actual = element.Current.BoundingRectangle;
        return !actual.IsEmpty && expected.Contains(actual);
    }
    private static bool ChatInputFocused()
    {
        return ChatInputFocused(AutomationElement.FocusedElement);
    }
    private static bool ChatInputFocused(AutomationElement focused)
    {
        if (!IsTextEntry(focused) || chatInputBounds.IsEmpty) return false;
        System.Windows.Rect bounds = focused.Current.BoundingRectangle;
        System.Windows.Rect window = targetRoot.Current.BoundingRectangle;
        System.Windows.Rect expected = chatInputBounds;
        expected.Offset(window.X - chatInputWindowBounds.X, window.Y - chatInputWindowBounds.Y);
        return !bounds.IsEmpty && expected.Contains(new System.Windows.Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2));
    }
    private static int ChatInputScore(string name, string id, string className, System.Windows.Rect bounds, System.Windows.Rect window)
    {
        if (bounds.IsEmpty || bounds.Width < 180 || bounds.Height < 12 ||
            !window.Contains(new System.Windows.Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2)) ||
            Regex.IsMatch(name + " " + id, @"search|filter|rename|검색|찾기|이름", RegexOptions.IgnoreCase)) return -1;
        bool known = Regex.IsMatch(id + " " + className, @"prompt-textarea|chat[-_]?input|composer|prosemirror", RegexOptions.IgnoreCase);
        bool labeled = Regex.IsMatch(name, @"message|reply|prompt|chat|질문|메시지|답장|입력|요청", RegexOptions.IgnoreCase);
        bool composerShape = bounds.Width >= 300 && bounds.Bottom >= window.Top + window.Height * 0.4;
        if (!known && !labeled && !composerShape) return -1;
        return (known ? 1000 : 0) + (labeled ? 300 : 0) + (composerShape ? 100 : 0) + (bounds.Height >= 40 ? 20 : 0);
    }
    private static CacheRequest InputMetadataCache()
    {
        CacheRequest cache = new CacheRequest();
        cache.TreeScope = TreeScope.Element;
        cache.Add(AutomationElement.NameProperty);
        cache.Add(AutomationElement.AutomationIdProperty);
        cache.Add(AutomationElement.ClassNameProperty);
        cache.Add(AutomationElement.BoundingRectangleProperty);
        cache.Add(AutomationElement.IsOffscreenProperty);
        cache.Add(AutomationElement.IsEnabledProperty);
        cache.Add(AutomationElement.ControlTypeProperty);
        cache.Add(AutomationElement.IsKeyboardFocusableProperty);
        cache.Add(AutomationElement.ProcessIdProperty);
        return cache;
    }
    private static void SaveChatInput(System.Windows.Rect window, System.Windows.Rect found)
    {
        chatInputBounds = found;
        chatInputWindowBounds = window;
        uint targetPid; GetWindowThreadProcessId(targetWindow, out targetPid);
        string[] values = { targetWindow.ToInt64().ToString(), targetPid.ToString(),
            window.Width.ToString(System.Globalization.CultureInfo.InvariantCulture),
            window.Height.ToString(System.Globalization.CultureInfo.InvariantCulture),
            (found.X - window.X).ToString(System.Globalization.CultureInfo.InvariantCulture),
            (found.Y - window.Y).ToString(System.Globalization.CultureInfo.InvariantCulture),
            found.Width.ToString(System.Globalization.CultureInfo.InvariantCulture),
            found.Height.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        // Only window and rectangle numbers are persisted for already-open popups.
        File.WriteAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "chat-input-" + app + ".txt"), values);
    }
    private static void RememberChatInput(AutomationElement root)
    {
        // Read only textbox metadata, never its value or conversation text.
        try
        {
            System.Windows.Rect window = root.Current.BoundingRectangle;
            try
            {
                AutomationElement focused = AutomationElement.FocusedElement;
                if (focused != null)
                {
                    AutomationElement metadata = focused.GetUpdatedCache(InputMetadataCache());
                    string id = metadata.Cached.AutomationId ?? "", className = metadata.Cached.ClassName ?? "";
                    if (!metadata.Cached.IsOffscreen && metadata.Cached.IsEnabled && metadata.Cached.IsKeyboardFocusable &&
                        metadata.Cached.ProcessId == root.Current.ProcessId &&
                        Regex.IsMatch(id + " " + className, @"prompt-textarea|chat[-_]?input|composer|prosemirror", RegexOptions.IgnoreCase) &&
                        ChatInputScore(metadata.Cached.Name ?? "", id, className, metadata.Cached.BoundingRectangle, window) >= 0)
                    {
                        SaveChatInput(window, metadata.Cached.BoundingRectangle);
                        trace.AppendLine("Chat input identified from focused composer metadata; full input lookup skipped.");
                        return;
                    }
                }
            }
            catch { } // A stale focus or non-composer still uses the original full lookup.
            System.Windows.Rect found = System.Windows.Rect.Empty;
            int bestScore = -1;
            bool ambiguous = false;
            int candidateCount = 0, acceptedCount = 0;
            CacheRequest cache = InputMetadataCache();
            using (cache.Activate())
                foreach (AutomationElement input in root.FindAll(TreeScope.Descendants,
                    new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                        new AndCondition(new PropertyCondition(AutomationElement.IsKeyboardFocusableProperty, true),
                            new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document),
                                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Custom),
                                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Pane))))))
                {
                    if (input.Cached.IsOffscreen || !input.Cached.IsEnabled) continue;
                    candidateCount++;
                    System.Windows.Rect bounds = input.Cached.BoundingRectangle;
                    int score = ChatInputScore(input.Cached.Name ?? "", input.Cached.AutomationId ?? "",
                        input.Cached.ClassName ?? "", bounds, window);
                    if (input.Cached.ControlType != ControlType.Edit &&
                        !Regex.IsMatch(input.Cached.AutomationId + " " + input.Cached.ClassName,
                            @"prompt-textarea|chat[-_]?input|composer|prosemirror", RegexOptions.IgnoreCase) &&
                        !Regex.IsMatch(input.Cached.Name ?? "", @"^(message|reply|prompt|chat input|질문|메시지|답장|입력|요청)", RegexOptions.IgnoreCase))
                        score = -1;
                    // Metadata only; never log the textbox name/value or its contents.
                    trace.AppendLine("Input candidate type=" + input.Cached.ControlType.ProgrammaticName +
                        ", id=" + input.Cached.AutomationId + ", class=" + input.Cached.ClassName +
                        ", bounds=" + bounds + ", score=" + score);
                    if (score < 0) continue;
                    acceptedCount++;
                    if (score > bestScore) { bestScore = score; found = bounds; ambiguous = false; }
                    else if (score == bestScore) ambiguous = true;
                }
            trace.AppendLine("Input candidates=" + candidateCount + ", accepted=" + acceptedCount + ", ambiguous=" + ambiguous);
            if (!found.IsEmpty && !ambiguous)
            {
                SaveChatInput(window, found);
                trace.AppendLine("Chat input identified from textbox metadata.");
                return;
            }
            if (chatInputBounds.IsEmpty) ReadRememberedChatInput(window);
        }
        catch { trace.AppendLine("Chat input metadata not available."); }
    }
    private static void ReadRememberedChatInput(System.Windows.Rect window)
    {
        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "chat-input-" + app + ".txt");
        if (!File.Exists(path)) return;
        string[] values = File.ReadAllLines(path);
        uint pid; GetWindowThreadProcessId(targetWindow, out pid);
        if (values.Length != 8 || values[0] != targetWindow.ToInt64().ToString() || values[1] != pid.ToString()) return;
        double[] numbers = new double[6];
        for (int i = 0; i < numbers.Length; i++)
            if (!Double.TryParse(values[i + 2], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out numbers[i]) ||
                Double.IsNaN(numbers[i]) || Double.IsInfinity(numbers[i])) return;
        if (Math.Abs(numbers[0] - window.Width) > 1 || Math.Abs(numbers[1] - window.Height) > 1) return;
        System.Windows.Rect bounds = new System.Windows.Rect(window.X + numbers[2], window.Y + numbers[3], numbers[4], numbers[5]);
        if (!window.Contains(bounds) || bounds.Width < 180 || bounds.Height < 12) return;
        chatInputBounds = bounds;
        chatInputWindowBounds = window;
        trace.AppendLine("Chat input position reused for the same window and size.");
    }
    private static bool TryInputClickPoint(System.Windows.Rect input, System.Drawing.Rectangle popup, out System.Drawing.Point point)
    {
        point = System.Drawing.Point.Empty;
        if (input.IsEmpty || input.Width < 24 || input.Height < 12) return false;
        foreach (double x in new[] { input.X + input.Width / 2, input.Left + 12, input.Right - 12 })
        {
            System.Drawing.Point candidate = new System.Drawing.Point((int)Math.Round(x),
                (int)Math.Round(input.Bottom - Math.Min(12, input.Height / 2)));
            if (!popup.Contains(candidate)) { point = candidate; return true; }
        }
        return false;
    }
    private static void FinishWithInputFocus(string message)
    {
        if (app == "chatgpt") { FinishGptWithInputFocus(message); return; }
        bool focused = false;
        try
        {
            EnsureForeground();
            // Focus alone must never skip dismissing a visible slider popup.
            bool sliderOpen = !claudeMenuFlow && ClaudeImageSlider.IsPopupVisible(targetWindow);
            if (sliderOpen) trace.AppendLine("Claude effort popup still open; dismissing it with the input click.");
            if (!sliderOpen && TextEntryFocused())
            {
                focused = ChatInputFocused();
                trace.AppendLine("Text entry already focused; no extra click sent.");
            }
            else
            {
                System.Windows.Rect window = targetRoot.Current.BoundingRectangle;
                if (chatInputBounds.IsEmpty) RememberChatInput(targetRoot);
                if (!chatInputBounds.IsEmpty && Math.Abs(window.Width - chatInputWindowBounds.Width) <= 1 &&
                    Math.Abs(window.Height - chatInputWindowBounds.Height) <= 1)
                {
                    // The Chat tab's menu can swallow the click that dismisses it,
                    // so that flow gets one verified retry; the slider popup gets none.
                    for (int attempt = 1; attempt <= (claudeMenuFlow ? 2 : 1) && !focused; attempt++)
                    {
                        ClickPoint(delegate
                        {
                            if (!sliderOpen && TextEntryFocused()) throw new OperationCanceledException("Input already focused; no extra click sent.");
                            System.Windows.Rect currentWindow = targetRoot.Current.BoundingRectangle;
                            if (Math.Abs(currentWindow.Width - window.Width) > 1 || Math.Abs(currentWindow.Height - window.Height) > 1)
                                throw new OperationCanceledException("Window resized before input focus; no click sent.");
                            System.Windows.Rect input = chatInputBounds;
                            input.Offset(currentWindow.X - chatInputWindowBounds.X, currentWindow.Y - chatInputWindowBounds.Y);
                            System.Drawing.Rectangle popup = app == "chatgpt" ? GptImageSlider.PopupBounds :
                                claudeMenuFlow ? claudeMenuBounds : ClaudeImageSlider.PopupBounds;
                            popup.Inflate(3, 3);
                            System.Drawing.Point point;
                            if (!TryInputClickPoint(input, popup, out point))
                                throw new OperationCanceledException("Chat input is covered by the reasoning popup; no click sent.");
                            return point;
                        }, true);
                        trace.AppendLine(attempt == 1
                            ? "Chat input clicked once at " + operationTimer.ElapsedMilliseconds + " ms" + (claudeMenuFlow ? "." : "; no further input will be sent.")
                            : "Chat input clicked again at " + operationTimer.ElapsedMilliseconds + " ms; no further input will be sent.");
                        Stopwatch focusTimer = Stopwatch.StartNew();
                        do
                        {
                            EnsureForeground();
                            focused = ChatInputFocused();
                            if (focused) break;
                            Thread.Sleep(10);
                        } while (focusTimer.ElapsedMilliseconds < 300);
                    }
                }
            }
        }
        catch (OperationCanceledException) { try { focused = ChatInputFocused(); } catch { } }
        catch { }
        trace.AppendLine("Chat input focus confirmed=" + focused);
        Record(message + (focused ? "; chat input focused" : "; chat input focus unconfirmed"));
    }
    private struct GptFocusState
    {
        internal bool PopupVisible, ChatFocused, OtherTextFocused;
        internal bool Complete { get { return !PopupVisible && ChatFocused; } }
    }
    private static GptFocusState CompleteGptFocus(Func<GptFocusState> readState, Action<int> clickInput,
        Func<long> elapsed, Action poll, Action<string> log)
    {
        GptFocusState state = readState();
        log("GPT finish initial: popup=" + state.PopupVisible + ", chat focus=" + state.ChatFocused);
        if (state.Complete || (!state.PopupVisible && state.OtherTextFocused)) return state;
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            clickInput(attempt);
            long started = elapsed(), completeSince = -1;
            do
            {
                state = readState();
                long now = elapsed();
                if (state.Complete)
                {
                    if (completeSince < 0) completeSince = now;
                    // Check two frames so a pending popup close/focus event
                    // cannot be mistaken for a finished transition.
                    if (now - completeSince >= 20) return state;
                }
                else completeSince = -1;
                if (!state.PopupVisible && state.OtherTextFocused) return state;
                if (now - started >= 300) break;
                poll();
            } while (true);
            log("GPT input click " + attempt + " result: popup=" + state.PopupVisible + ", chat focus=" + state.ChatFocused);
            if (attempt == 1) log("GPT dismissal/focus unconfirmed; one verified retry.");
        }
        return state;
    }
    private static GptFocusState ReadGptFocusState(bool fullSearch)
    {
        EnsureForeground();
        bool popup = fullSearch ? GptImageSlider.IsPopupVisible(targetWindow) : GptImageSlider.IsPopupVisibleAtLastLocation(targetWindow);
        AutomationElement focused = AutomationElement.FocusedElement;
        bool chat = ChatInputFocused(focused);
        return new GptFocusState { PopupVisible = popup, ChatFocused = chat, OtherTextFocused = !chat && IsTextEntry(focused) };
    }
    private static void FinishGptWithInputFocus(string message)
    {
        GptFocusState final = new GptFocusState { PopupVisible = true };
        bool stateRead = false;
        try
        {
            EnsureForeground();
            if (chatInputBounds.IsEmpty) RememberChatInput(targetRoot);
            System.Windows.Rect initialWindow = targetRoot.Current.BoundingRectangle;
            if (chatInputBounds.IsEmpty || Math.Abs(initialWindow.Width - chatInputWindowBounds.Width) > 1 ||
                Math.Abs(initialWindow.Height - chatInputWindowBounds.Height) > 1)
            {
                trace.AppendLine("GPT finish skipped: chat input position unavailable for this window size.");
                final = ReadGptFocusState(true);
                stateRead = true;
            }
            else
            {
                bool initialRead = true;
                final = CompleteGptFocus(delegate
                {
                    GptFocusState state = ReadGptFocusState(initialRead);
                    initialRead = false;
                    final = state;
                    stateRead = true;
                    return state;
                }, delegate(int attempt)
                {
                    ClickPoint(delegate
                    {
                        // Recheck the popup before every actual click. Focus
                        // alone must never skip dismissing a visible GPT menu.
                        bool visible = GptImageSlider.IsPopupVisible(targetWindow);
                        if (!visible && TextEntryFocused())
                            throw new OperationCanceledException("Popup dismissed and text entry focused; no extra input click.");
                        System.Windows.Rect currentWindow = targetRoot.Current.BoundingRectangle;
                        if (Math.Abs(currentWindow.Width - initialWindow.Width) > 1 || Math.Abs(currentWindow.Height - initialWindow.Height) > 1)
                            throw new OperationCanceledException("Window resized before GPT input focus; no click sent.");
                        System.Windows.Rect input = chatInputBounds;
                        input.Offset(currentWindow.X - chatInputWindowBounds.X, currentWindow.Y - chatInputWindowBounds.Y);
                        System.Drawing.Rectangle popup = visible ? GptImageSlider.PopupBounds : System.Drawing.Rectangle.Empty;
                        if (visible) popup.Inflate(3, 3);
                        System.Drawing.Point point;
                        if (!TryInputClickPoint(input, popup, out point))
                            throw new OperationCanceledException("Chat input is covered by GPT's popup; no click sent.");
                        return point;
                    }, true);
                    trace.AppendLine("GPT chat input click " + attempt + " at " + operationTimer.ElapsedMilliseconds + " ms.");
                }, delegate { return operationTimer.ElapsedMilliseconds; }, delegate { Thread.Sleep(10); }, delegate(string value) { trace.AppendLine(value); });
            }
        }
        catch (OperationCanceledException error)
        {
            trace.AppendLine("GPT finish stopped: " + error.Message);
            try { if (GetForegroundWindow() == targetWindow) { final = ReadGptFocusState(true); stateRead = true; } } catch { }
        }
        catch (Exception error) { trace.AppendLine("GPT finish read/click failed: " + error.GetType().Name); }
        trace.AppendLine("GPT popup closed confirmed=" + (stateRead && !final.PopupVisible) + "; chat input focus confirmed=" + (stateRead && final.ChatFocused));
        Record(message + (!stateRead ? "; popup/input focus unconfirmed" : final.Complete ? "; popup closed; chat input focused" :
            final.PopupVisible ? "; reasoning popup still open; chat input focus unconfirmed" : "; popup closed; chat input focus unconfirmed"));
    }
    private static void GptFocusSelfTest()
    {
        // Replay retained textbox focus, a missed outside click, and a pending
        // focus event without any window, screenshot or injected input.
        for (int scenario = 0; scenario < 7; scenario++)
        {
            int clicks = 0;
            long clock = 0;
            int test = scenario;
            GptFocusState result = CompleteGptFocus(delegate
            {
                if (test == 0) return new GptFocusState { ChatFocused = true };
                if (test == 3) return new GptFocusState { OtherTextFocused = true };
                if (test == 4) return new GptFocusState { PopupVisible = true, ChatFocused = true };
                if (clicks == 0) return new GptFocusState { PopupVisible = true, ChatFocused = test == 1 };
                if (test == 2 && clicks == 1) return new GptFocusState { PopupVisible = true };
                if (test == 5 && clicks == 1) return new GptFocusState();
                if (test == 6 && clicks == 1) return new GptFocusState { ChatFocused = clock < 20 };
                return new GptFocusState { ChatFocused = true };
            }, delegate(int attempt) { clicks++; }, delegate { return clock; },
                delegate { clock += 10; }, delegate(string value) { });
            int expectedClicks = test == 0 || test == 3 ? 0 : test == 1 ? 1 : 2;
            bool expectedComplete = test != 3 && test != 4;
            if (clicks != expectedClicks || result.Complete != expectedComplete || (test == 4 && !result.PopupVisible))
                throw new InvalidOperationException("GPT popup/input completion regression failed: scenario " + test);
        }
    }
    private static void WriteDiagnostic(Exception error)
    {
        try
        {
            StringBuilder details = new StringBuilder();
            details.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + app + " " + command + " build=" + BuildName);
            details.AppendLine(error.GetType().Name + ": " + error.Message);
            details.Append(trace);
            // Only model/effort controls; never dump chat text, editors, or the whole UI.
            if (targetRoot != null)
                foreach (ControlType type in choiceTypes)
                    foreach (AutomationElement item in Elements(targetRoot, type))
                    {
                        string name = Name(item);
                        if (!IsEffortChoice(name) && !IsEffortTrigger(name) && !(app == "claude" && ClaudeEffort(name) >= 0 && name.Length < 60) &&
                            !Regex.IsMatch(name, ClaudeModelPattern + @"|^(?:GPT[\s-]*)?6\.1[\s-]+Sol\b", RegexOptions.IgnoreCase)) continue;
                        if (name.Length > 180) name = name.Substring(0, 180);
                        details.Append(type.ProgrammaticName + " name=" + name + " patterns=");
                        foreach (AutomationPattern pattern in item.GetSupportedPatterns()) details.Append(pattern.ProgrammaticName + " ");
                        details.AppendLine();
                    }
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "last-diagnostic.txt"), details.ToString());
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "last-diagnostic-" + app + ".txt"), details.ToString());
        }
        catch { }
    }
    private static void Record(string message)
    {
        try
        {
            string result = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + app + " " + message + Environment.NewLine;
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "last-result.txt"), result);
            trace.AppendLine(StrokesImageSearch.Summary);
            string action = result + "build=" + BuildName + "; elapsed=" + operationTimer.ElapsedMilliseconds + " ms" + Environment.NewLine + trace;
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "last-action.txt"), action);
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "last-result-" + app + ".txt"), result);
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "last-action-" + app + ".txt"), action);
        }
        catch { }
    }
    private static void ShowNotice(string message)
    {
        if (GetForegroundWindow() != targetWindow) return;
        using (Notice notice = new Notice(message)) Application.Run(notice);
    }
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
            StartPosition = FormStartPosition.Manual; Width = 420; Height = 78;
            System.Drawing.Rectangle area = Screen.FromHandle(targetWindow).WorkingArea;
            Left = area.Right - Width - 20; Top = area.Bottom - Height - 20;
            BackColor = System.Drawing.Color.FromArgb(35, 35, 40);
            Controls.Add(new Label { Dock = DockStyle.Fill, Text = message, ForeColor = System.Drawing.Color.White,
                Font = new System.Drawing.Font("Malgun Gothic", 10), Padding = new Padding(12) });
            System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 2400 };
            timer.Tick += delegate { timer.Stop(); Close(); };
            timer.Start(); FormClosed += delegate { timer.Dispose(); };
        }
    }
    private static int SelfTest()
    {
        StrokesImageSearch.SelfTest();
        SelectionTranslator.SelfTest();
        BacktickClick.SelfTest();
        GptFocusSelfTest();
        ForegroundSelfTest();
        // Protect the final click from search fields, off-window edits and popup overlap.
        System.Windows.Rect testWindow = new System.Windows.Rect(0, 0, 1000, 900);
        System.Windows.Rect testInput = new System.Windows.Rect(200, 600, 600, 100);
        if (ChatInputScore("Search", "prompt-textarea", "", testInput, testWindow) >= 0 ||
            ChatInputScore("", "", "", new System.Windows.Rect(200, 20, 600, 30), testWindow) >= 0 ||
            ChatInputScore("Message", "prompt-textarea", "", new System.Windows.Rect(1200, 600, 600, 100), testWindow) >= 0 ||
            ChatInputScore("Message", "prompt-textarea", "", testInput, testWindow) < 0)
            throw new InvalidOperationException("Chat input identification selftest failed.");
        System.Windows.Rect singleLineInput = new System.Windows.Rect(200, 650, 600, 18);
        if (ChatInputScore("", "", "ProseMirror", singleLineInput, testWindow) < 0 ||
            ChatInputScore("Search", "", "", singleLineInput, testWindow) >= 0)
            throw new InvalidOperationException("Single-line Claude input must be accepted without matching search.");
        System.Drawing.Point inputPoint;
        System.Drawing.Rectangle testPopup = new System.Drawing.Rectangle(450, 580, 400, 140);
        if (!TryInputClickPoint(testInput, testPopup, out inputPoint) || testPopup.Contains(inputPoint) ||
            !testInput.Contains(new System.Windows.Point(inputPoint.X, inputPoint.Y)) ||
            TryInputClickPoint(testInput, new System.Drawing.Rectangle(190, 590, 620, 120), out inputPoint))
            throw new InvalidOperationException("Chat input click must avoid the popup.");
        if (!TryInputClickPoint(singleLineInput, System.Drawing.Rectangle.Empty, out inputPoint) ||
            !singleLineInput.Contains(new System.Windows.Point(inputPoint.X, inputPoint.Y)))
            throw new InvalidOperationException("Short Claude input click must stay inside its text area.");
        if (Effort("6.1 Sol High") != 1 || Effort("6.1 Sol Extra High") != 2 || Effort("Opus 5.5 Medium") != 0 ||
            Effort("xHigh") != 2 || Effort("highest") != -1 || Effort("매우 높음") != 2 ||
            Effort("노력: 엑스트라") != 2 || Effort("노력: 높음") != 1 || Effort("노력: 보통") != 0 ||
            EffortOption("Extra High", 1) || !EffortOption("Extra High", 2)) return 1;
        // Sessions page tab names; Markdown view button names.
        if (!IsTasksLabel("백그라운드 작업") || !IsTasksLabel("백그라운드 작업 4") || !IsTasksLabel("Background tasks 12") ||
            IsTasksLabel("백그라운드 작업 지우기") || IsTasksLabel("채팅 및 백그라운드 작업 크기 조정"))
            throw new InvalidOperationException("Background tasks label selftest failed.");
        if (!IsArchivedTab("보관됨") || !IsArchivedTab("Archived") || IsArchivedTab("활성") || IsArchivedTab("보관됨 검색용 2") ||
            !IsMarkdownViewToggle("소스 보기") || !IsMarkdownViewToggle("미리보기") || IsMarkdownViewToggle("확장 프로그램 미리보기 실패"))
            throw new InvalidOperationException("Sessions page or Markdown view selftest failed.");
        string[] korean = { "노력: 낮음", "노력: 중간", "노력: 높음", "노력: 엑스트라", "노력: 최대", "노력: Ultracode" };
        string[] english = { "Effort: Low", "Effort: Medium", "Effort: High", "Effort: Extra", "Effort: Max", "Effort: Ultracode" };
        for (int level = 0; level < 6; level++)
        {
            if (ClaudeEffort(korean[level]) != level || ClaudeEffort(english[level]) != level ||
                NextClaude(level, 1) != (level == 5 ? 5 : level + 1) ||
                NextClaude(level, -1) != (level == 0 ? 0 : level - 1)) return 1;
        }
        if (ClaudeEffort("노력: 보통") != 1 || ClaudeEffort("노력: 매우 높음") != 3 ||
            ClaudeEffort("노력: xHigh") != 3 || ClaudeEffort("노력: 울트라코드") != 5 ||
            ClaudeEffort("unknown") != -1) return 1;
        string[] gpt = { "GPT-6.1 Sol Light", "GPT-6.1 Sol Medium", "GPT-6.1 Sol High", "GPT-6.1 Sol Extra High", "GPT-6.1 Sol Ultra" };
        for (int level = 0; level < 5; level++)
            if (GptEffort(gpt[level]) != level || NextGpt(level, 1) != (level == 4 ? 4 : level + 1) ||
                NextGpt(level, -1) != (level == 0 ? 0 : level - 1)) return 1;
        if (GptEffort("GPT-6.1 Sol") != -1) return 1;
        string[] modelNames = { "모델: Opus 5.5", "Model: Opus 5.5", "Opus 5.5", " Opus 5.5" };
        string[] sessionNames = { "유휴 가이드 Opus 5.5 업그레이드", "가이드 Opus 5.5 업그레이드에 대한 더 많은 옵션", "Opus 5.5 업그레이드에 대한 더 많은 옵션" };
        foreach (string name in modelNames) if (!Regex.IsMatch(name, ClaudeModelPattern, RegexOptions.IgnoreCase)) return 1;
        foreach (string name in sessionNames) if (Regex.IsMatch(name, ClaudeModelPattern, RegexOptions.IgnoreCase)) return 1;
        // Another model's control means the tree is ready; frame buttons alone mean it is not.
        foreach (string name in new[] { "모델: Opus 5.5", "모델: Sonnet 5.5", "Model: Haiku 5.5", " model : Opus 5.5" }) if (!IsModelControl(name)) return 1;
        foreach (string name in new[] { "최소화", "최대화", "닫기", "노력: 높음", "Opus 5.5", sessionNames[0] }) if (IsModelControl(name)) return 1;
        // Effort submenu options as listed; Ultracode is the Code tab's sixth and top step.
        string[] chatOptions = { "낮음", "중간 추천", "높음", "엑스트라", "최대 3.5× 이상 사용량", "Ultracode" };
        string[] chatOptionsEnglish = { "Low", "Medium Recommended", "High", "Extra", "Max 4x+ usage", "Ultracode" };
        for (int level = 0; level < 6; level++)
            if (ChatEffortOption(chatOptions[level]) != level || ChatEffortOption(chatOptionsEnglish[level]) != level ||
                NextClaudeMenu(level, 1, 5) != (level == 5 ? 5 : level + 1) || NextClaudeMenu(level, -1, 5) != (level == 0 ? 0 : level - 1) ||
                (level < 5 && (NextClaudeMenu(level, 1, 4) != (level == 4 ? 4 : level + 1) || NextClaudeMenu(level, -1, 4) != (level == 0 ? 0 : level - 1)))) return 1;
        bool chatCeiling = false;
        try { NextClaudeMenu(5, -1, 4); } catch (InvalidOperationException) { chatCeiling = true; }
        if (!chatCeiling || ClaudeEffort("모델: Opus 5.5 Ultracode") != 5 || ClaudeEffort("모델: Opus 5.5 높음") != 2) return 1;
        foreach (string name in new[] { "노력 중간", "Opus 5.5 복잡한 작업과 일상 업무를 위해", "더 많은 모델", "파일", "최소화", "Lower", "Maxwell" })
            if (ChatEffortOption(name) != -1) return 1;
        if (!IsEffortTrigger("노력 중간") || ClaudeEffort("노력 중간") != 1 || ClaudeEffort("모델: Opus 5.5 엑스트라") != 3 ||
            ClaudeEffort("모델: Opus 5.5") != -1) return 1;
        if (!IsSupportedGptMode("모드 전환, 현재 모드: Codex") || !IsSupportedGptMode("모드 전환, 현재 모드: ChatGPT") ||
            !IsSupportedGptMode("Switch mode, current mode: ChatGPT") || IsSupportedGptMode("모드 전환, 현재 모드: 이미지") ||
            IsSupportedGptMode("ChatGPT")) return 1;
        bool rejected = false;
        try { NextClaude(-1, 1); } catch (InvalidOperationException) { rejected = true; }
        return rejected && ClaudeImageSlider.SelfTest() && GptImageSlider.SelfTest() ? 0 : 1;
    }
}
