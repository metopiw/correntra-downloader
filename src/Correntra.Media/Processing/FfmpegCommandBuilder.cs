namespace Correntra.Media.Processing;

public enum AudioOutputFormat
{
    Original,
    M4a,
    Mp3,
    Flac,
    OggOpus,
}

public static class FfmpegCommandBuilder
{
    public static IReadOnlyList<string> BuildMux(
        string videoPath,
        string audioPath,
        string outputPath,
        string? subtitlePath = null)
    {
        ValidatePath(videoPath, nameof(videoPath));
        ValidatePath(audioPath, nameof(audioPath));
        ValidatePath(outputPath, nameof(outputPath));

        var arguments = CommonPrefix();
        arguments.AddRange(["-i", videoPath, "-i", audioPath]);
        if (!string.IsNullOrWhiteSpace(subtitlePath))
        {
            ValidatePath(subtitlePath, nameof(subtitlePath));
            arguments.AddRange(["-i", subtitlePath]);
        }

        arguments.AddRange(["-map", "0:v:0", "-map", "1:a:0"]);
        if (!string.IsNullOrWhiteSpace(subtitlePath))
        {
            arguments.AddRange(["-map", "2:0?", "-c:s", "mov_text"]);
        }

        arguments.AddRange(["-c:v", "copy", "-c:a", "copy", "-movflags", "+faststart", outputPath]);
        return arguments;
    }

    public static IReadOnlyList<string> BuildRemux(string inputPath, string outputPath)
    {
        ValidatePath(inputPath, nameof(inputPath));
        ValidatePath(outputPath, nameof(outputPath));
        var arguments = CommonPrefix();
        arguments.AddRange(["-i", inputPath, "-map", "0", "-c", "copy", "-movflags", "+faststart", outputPath]);
        return arguments;
    }

    public static IReadOnlyList<string> BuildAudioConversion(
        string inputPath,
        string outputPath,
        AudioOutputFormat format,
        int bitrateKbps = 192,
        IReadOnlyDictionary<string, string>? metadata = null,
        string? coverPath = null)
    {
        ValidatePath(inputPath, nameof(inputPath));
        ValidatePath(outputPath, nameof(outputPath));
        if (bitrateKbps is < 32 or > 512)
        {
            throw new ArgumentOutOfRangeException(nameof(bitrateKbps));
        }

        var arguments = CommonPrefix();
        arguments.AddRange(["-i", inputPath]);
        if (!string.IsNullOrWhiteSpace(coverPath))
        {
            ValidatePath(coverPath, nameof(coverPath));
            arguments.AddRange(["-i", coverPath, "-map", "0:a:0", "-map", "1:v:0", "-disposition:v:0", "attached_pic"]);
        }
        else
        {
            arguments.AddRange(["-map", "0:a:0"]);
        }

        switch (format)
        {
            case AudioOutputFormat.Original:
                arguments.AddRange(["-c:a", "copy"]);
                break;
            case AudioOutputFormat.M4a:
                arguments.AddRange(["-c:a", "aac", "-b:a", $"{bitrateKbps}k"]);
                break;
            case AudioOutputFormat.Mp3:
                arguments.AddRange(["-c:a", "libmp3lame", "-b:a", $"{bitrateKbps}k"]);
                break;
            case AudioOutputFormat.Flac:
                arguments.AddRange(["-c:a", "flac"]);
                break;
            case AudioOutputFormat.OggOpus:
                arguments.AddRange(["-c:a", "libopus", "-b:a", $"{bitrateKbps}k"]);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }

        if (metadata is not null)
        {
            foreach ((string key, string value) in metadata.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                if (IsSafeMetadata(key, value))
                {
                    arguments.AddRange(["-metadata", $"{key}={value}"]);
                }
            }
        }

        arguments.Add(outputPath);
        return arguments;
    }

    private static List<string> CommonPrefix()
    {
        return ["-hide_banner", "-nostdin", "-y", "-loglevel", "warning", "-progress", "pipe:2"];
    }

    private static void ValidatePath(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0'))
        {
            throw new ArgumentException("A valid path is required.", parameterName);
        }
    }

    private static bool IsSafeMetadata(string key, string value)
    {
        return key.Length is > 0 and <= 64 &&
               value.Length <= 4096 &&
               key.All(character => char.IsLetterOrDigit(character) || character is '_' or '-') &&
               !value.Contains('\0');
    }
}

