using System;

namespace FlyleafLib.Controls.WPF;

public static class DebugLogger
{
    private static readonly bool IsEnabled = false;

    public static void Print(string message)
    {
        if (IsEnabled)
            Console.WriteLine(message);
    }
}
