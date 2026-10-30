// CodexClaudeReasoningHotkeys-v1 -- user-requested translation of the selection to Korean in any app.
var reasoningHelper = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments), "_Utility", "claude-desktop-hotkeys", "ReasoningSwitch.exe");
// Every app: the helper copies the selection itself, so the chord is not passed through.
sp.RunProgram(reasoningHelper, "translate", "open", "normal", true, false, false);
