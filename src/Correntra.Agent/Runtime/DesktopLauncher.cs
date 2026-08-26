using System.Diagnostics;
using Correntra.Core;
using Correntra.Infrastructure.Ipc;

namespace Correntra.Agent.Runtime;

public interface IDesktopLauncher
{
    Task<bool> ShowDownloadConfirmationAsync(JobId jobId, CancellationToken cancellationToken = default);
}

public sealed class DesktopLauncher : IDesktopLauncher
{
    private readonly IDesktopActivationClient _activationClient;
    private readonly string? _desktopExecutablePath;
    private readonly string _baseDirectory;
    private readonly object _sync = new();
    private DateTimeOffset _lastLaunchAtUtc = DateTimeOffset.MinValue;

    public DesktopLauncher(
        string? desktopExecutablePath = null,
        IDesktopActivationClient? activationClient = null,
        string? baseDirectory = null)
    {
        _desktopExecutablePath = desktopExecutablePath;
        _activationClient = activationClient ?? new DesktopActivationClient();
        _baseDirectory = baseDirectory ?? AppContext.BaseDirectory;
    }

    public async Task<bool> ShowDownloadConfirmationAsync(JobId jobId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (jobId.IsEmpty)
        {
            return false;
        }

        string jobIdText = jobId.ToString();

        // Preferred path: ask the already-running desktop shell to surface the
        // confirmation modal. No second process, and the existing window comes
        // to the foreground.
        if (await _activationClient.TryConfirmDownloadAsync(jobIdText, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        // Fallback: launch a fresh desktop instance with --confirm-download.
        string? executablePath = DesktopExecutableLocator.Resolve(_baseDirectory, _desktopExecutablePath);
        if (executablePath is null)
        {
            return false;
        }

        lock (_sync)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (now - _lastLaunchAtUtc < TimeSpan.FromSeconds(2))
            {
                return true;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(executablePath)!,
            };
            startInfo.ArgumentList.Add("--confirm-download");
            startInfo.ArgumentList.Add(jobIdText);
            try
            {
                using Process? process = Process.Start(startInfo);
                _lastLaunchAtUtc = now;
                return process is not null;
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }
    }
}
