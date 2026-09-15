using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Correntra.StartLauncher;

/// <summary>Launches the repository's normal <c>baslat.bat</c> entry point.</summary>
internal static class Program
{
    [STAThread]
    private static int Main()
    {
        string batchPath = Path.Combine(AppContext.BaseDirectory, "baslat.bat");
        if (!File.Exists(batchPath))
        {
            ShowError("baslat.bat bu EXE'nin yanında bulunamadı.");
            return 1;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = batchPath,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = true,
            });
            return 0;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ShowError("Correntra başlatılamadı. " + exception.Message);
            return 1;
        }
    }

    private static void ShowError(string message)
    {
        if (MessageBoxW(IntPtr.Zero, message, "Correntra Başlat", 0x00000010) == 0)
        {
            Trace.WriteLine("Correntra Başlat error dialog could not be shown.");
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
