using System.Diagnostics;
using System.Text;

namespace Correntra.Media.Processing;

public sealed record FfmpegInspectionResult(
    bool IsUsable,
    bool IsLgplCompatible,
    string VersionLine,
    string BuildConfiguration,
    string LicenseText,
    string? FailureReason);

public sealed class FfmpegInspector
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static async Task<FfmpegInspectionResult> InspectAsync(string executablePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return new(false, false, string.Empty, string.Empty, string.Empty, "FFmpeg executable was not found.");
        }

        ProcessResult versionResult;
        ProcessResult buildResult;
        ProcessResult licenseResult;
        try
        {
            versionResult = await RunAsync(executablePath, ["-hide_banner", "-version"], cancellationToken)
                .ConfigureAwait(false);
            buildResult = await RunAsync(executablePath, ["-hide_banner", "-buildconf"], cancellationToken)
                .ConfigureAwait(false);
            licenseResult = await RunAsync(executablePath, ["-hide_banner", "-L"], cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new(false, false, string.Empty, string.Empty, string.Empty, exception.Message);
        }

        string versionText = Combine(versionResult);
        string buildText = Combine(buildResult);
        string licenseText = Combine(licenseResult);
        return EvaluateReportedConfiguration(
            versionText,
            buildText,
            licenseText,
            versionResult.ExitCode == 0 && buildResult.ExitCode == 0 && licenseResult.ExitCode == 0);
    }

    internal static FfmpegInspectionResult EvaluateReportedConfiguration(
        string versionText,
        string buildText,
        string licenseText,
        bool commandsSucceeded)
    {
        string combined = string.Join(Environment.NewLine, versionText, buildText, licenseText);
        string versionLine = combined.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.StartsWith("ffmpeg version", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        bool hasGpl = combined.Contains("--enable-gpl", StringComparison.OrdinalIgnoreCase);
        bool hasNonFree = combined.Contains("--enable-nonfree", StringComparison.OrdinalIgnoreCase);
        bool claimsLgpl = combined.Contains("GNU Lesser General Public License", StringComparison.OrdinalIgnoreCase) ||
                          combined.Contains("LGPL", StringComparison.OrdinalIgnoreCase);
        bool usable = commandsSucceeded && versionLine.Length > 0;
        bool compatible = usable && !hasGpl && !hasNonFree && claimsLgpl;

        string? failure = !usable
            ? "FFmpeg could not be executed or did not report a version."
            : hasNonFree
                ? "This FFmpeg build contains --enable-nonfree and cannot be distributed."
                : hasGpl
                    ? "This FFmpeg build contains --enable-gpl and is not allowed in Correntra releases."
                    : !claimsLgpl
                        ? "The FFmpeg build did not provide a verifiable LGPL license statement."
                        : null;

        return new(usable, compatible, versionLine, buildText, licenseText, failure);
    }

    private static string Combine(ProcessResult result) =>
        string.Concat(result.StandardOutput, Environment.NewLine, result.StandardError);

    private static async Task<ProcessResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("FFmpeg process could not be started.");
        }

        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = new CancellationTokenSource(Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            process.Kill(true);
            throw new TimeoutException("FFmpeg inspection timed out.");
        }

        return new ProcessResult(process.ExitCode, await standardOutput.ConfigureAwait(false), await standardError.ConfigureAwait(false));
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
