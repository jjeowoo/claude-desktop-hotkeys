using System;

// Entry point. Backtick click and the capture locator are routed here before ReasoningSwitch is
// touched: that class's static fields load UI Automation, which neither needs.
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "backtick-click") return BacktickClick.Toggle();
        if (args.Length == 2 && args[0] == "backtick-notice") return BacktickClick.Notify(args[1]);
        if (args.Length >= 1 && args[0] == "locate-capture") return CaptureLocator.Run(args);
        return ReasoningSwitch.Run(args);
    }
}
