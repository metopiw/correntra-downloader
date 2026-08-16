using System.Diagnostics;

namespace Correntra.Platform.Windows.Power;

public enum WindowsPowerAction
{
    None,
    Sleep,
    Hibernate,
    ShutDown,
}

public static class WindowsPowerActions
{
    public static Process? Execute(WindowsPowerAction action)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        return action switch
        {
            WindowsPowerAction.None => null,
            WindowsPowerAction.Sleep => Start("rundll32.exe", ["powrprof.dll,SetSuspendState", "0,1,0"]),
            WindowsPowerAction.Hibernate => Start("shutdown.exe", ["/h"]),
            WindowsPowerAction.ShutDown => Start("shutdown.exe", ["/s", "/t", "0"]),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }

    private static Process Start(string fileName, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo) ?? throw new InvalidOperationException("The Windows power action could not be started.");
    }
}

