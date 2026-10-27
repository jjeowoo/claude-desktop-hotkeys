// CodexClaudeReasoningHotkeys-v1 -- user-requested active-app reasoning control.
var reasoningHelper = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments), "_ETC", "claude-desktop-hotkeys", "ReasoningSwitch.exe");
var reasoningApp = '';
try { reasoningApp = sp.ForegroundWindow().Process.ProcessName.toLowerCase(); } catch (e) {}
if (reasoningApp === 'chatgpt' || reasoningApp === 'claude') {
    sp.RunProgram(reasoningHelper, "up", "open", "hidden", true, false, false);
} else {
    // Pass the original chord through to other apps; S+ ignores its own injected keys.
    sp.SendModifiedVKeys([vk.LCONTROL, vk.LMENU], [vk.UP]);
}
