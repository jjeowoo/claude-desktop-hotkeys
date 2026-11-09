// CodexClaudeReasoningHotkeys-v1 -- user-requested Claude Code changes view toggle on Ctrl+Shift+G, in place of Claude's own Ctrl+Shift+D.
var reasoningHelper = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments), "_repos", "claude-desktop-hotkeys", "ReasoningSwitch.exe");
var reasoningApp = '';
try { reasoningApp = sp.ForegroundWindow().Process.ProcessName.toLowerCase(); } catch (e) {}
if (reasoningApp === 'claude') {
    sp.RunProgram(reasoningHelper, "changes", "open", "hidden", true, false, false);
} else {
    // Pass the original chord through to other apps; S+ ignores its own injected keys.
    sp.SendModifiedVKeys([vk.LCONTROL, vk.LSHIFT], [vk.VK_G]);
}
