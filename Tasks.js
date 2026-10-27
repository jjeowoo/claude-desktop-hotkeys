// CodexClaudeReasoningHotkeys-v1 -- user-requested Claude Code background tasks pane toggle.
var reasoningHelper = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments), "_ETC", "claude-desktop-hotkeys", "ReasoningSwitch.exe");
var reasoningApp = '';
try { reasoningApp = sp.ForegroundWindow().Process.ProcessName.toLowerCase(); } catch (e) {}
if (reasoningApp === 'claude') {
    sp.RunProgram(reasoningHelper, "tasks", "open", "hidden", true, false, false);
} else {
    // Pass the original chord through to other apps; S+ ignores its own injected keys.
    sp.SendModifiedVKeys([vk.LCONTROL, vk.LMENU], [vk.VK_B]);
}
