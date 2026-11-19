using System;

// Entry point. Backtick click, the capture locator and the focus hold are routed here before ReasoningSwitch is
// touched: that class's static fields load UI Automation, which none of them needs.
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "backtick-click") return BacktickClick.Toggle();
        if (args.Length == 2 && args[0] == "backtick-notice") return BacktickClick.Notify(args[1]);
        if (args.Length >= 1 && args[0] == "locate-capture") return CaptureLocator.Run(args);
        if (args.Length == 1 && args[0] == "hold-focus") return FocusHold.Run();
        int testHoldMs;
        if (args.Length == 2 && args[0] == "hold-focus" && int.TryParse(args[1], out testHoldMs)) return FocusHold.Run(testHoldMs);
        return ReasoningSwitch.Run(args);
    }
}
