using System.Diagnostics;
using System.IO;
using System.Text;

namespace LivestreamStudio;

public static class BroadcastService
{
    public static bool IsValidRtmpServerUrl(string serverUrl)
    {
        return Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri)
            && uri.Scheme is "rtmp" or "rtmps"
            && !string.IsNullOrWhiteSpace(uri.Host)
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment);
    }

    public static string? FindFfmpegExecutable()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WinGet", "Links", "ffmpeg.exe")
        };

        var searchPaths = new[]
        {
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine)
        };
        foreach (var pathList in searchPaths)
        {
            if (string.IsNullOrWhiteSpace(pathList))
            {
                continue;
            }

            candidates.AddRange(pathList.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(path => Path.Combine(path.Trim(), "ffmpeg.exe")));
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    public static ProcessStartInfo CreateStartInfo(
        FeedScene feed,
        CameraDevice? camera,
        string? audioDevice,
        bool includeMicrophoneAudio,
        IReadOnlyCollection<Destination> destinations)
    {
        if (destinations.Count == 0)
        {
            throw new InvalidOperationException("Select at least one configured streaming destination.");
        }

        var ffmpegPath = FindFfmpegExecutable()
            ?? throw new FileNotFoundException("FFmpeg is not installed. Install FFmpeg and restart Mosaic Studio.");
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true
        };
        var arguments = startInfo.ArgumentList;
        arguments.Add("-hide_banner");
        arguments.Add("-loglevel");
        arguments.Add("warning");
        arguments.Add("-nostats");
        arguments.Add("-stats_period");
        arguments.Add("1");
        arguments.Add("-progress");
        arguments.Add("pipe:1");

        if (!string.IsNullOrWhiteSpace(feed.CameraDevicePath))
        {
            if (camera is null)
            {
                throw new InvalidOperationException("Refresh the camera list and select an available camera feed.");
            }

            if (includeMicrophoneAudio && string.IsNullOrWhiteSpace(audioDevice))
            {
                throw new InvalidOperationException("Select an available microphone or turn off microphone audio.");
            }

            arguments.Add("-thread_queue_size");
            arguments.Add("512");
            arguments.Add("-f");
            arguments.Add("dshow");
            arguments.Add("-video_size");
            arguments.Add("1280x720");
            arguments.Add("-framerate");
            arguments.Add("30");
            arguments.Add("-i");
            arguments.Add(includeMicrophoneAudio
                ? $"video=\"{EscapeDeviceName(camera.Name)}\":audio=\"{EscapeDeviceName(audioDevice!)}\""
                : $"video=\"{EscapeDeviceName(camera.Name)}\"");
        }
        else if (feed.VideoPath is { } videoPath && File.Exists(videoPath))
        {
            arguments.Add("-re");
            arguments.Add("-stream_loop");
            arguments.Add("-1");
            arguments.Add("-i");
            arguments.Add(videoPath);
        }
        else
        {
            throw new InvalidOperationException("Select a connected camera feed or an existing video before broadcasting.");
        }

        arguments.Add("-map");
        arguments.Add("0:v:0");
        arguments.Add("-map");
        arguments.Add("0:a:0?");
        arguments.Add("-c:v");
        arguments.Add("libopenh264");
        arguments.Add("-profile:v");
        arguments.Add("main");
        arguments.Add("-rc_mode");
        arguments.Add("bitrate");
        arguments.Add("-allow_skip_frames");
        arguments.Add("1");
        arguments.Add("-b:v");
        arguments.Add("4500k");
        arguments.Add("-maxrate");
        arguments.Add("4500k");
        arguments.Add("-bufsize");
        arguments.Add("9000k");
        arguments.Add("-g");
        arguments.Add("60");
        arguments.Add("-pix_fmt");
        arguments.Add("yuv420p");
        arguments.Add("-c:a");
        arguments.Add("aac");
        arguments.Add("-b:a");
        arguments.Add("160k");
        arguments.Add("-ar");
        arguments.Add("44100");
        arguments.Add("-f");
        arguments.Add("tee");

        var targets = destinations.Select(destination =>
        {
            if (!destination.IsConfigured || !IsValidRtmpServerUrl(destination.ServerUrl))
            {
                throw new InvalidOperationException($"Set up a valid RTMP server and stream key for {destination.Name}.");
            }

            var streamKey = destination.GetStreamKey();
            var streamUrl = $"{destination.ServerUrl.TrimEnd('/')}/{streamKey}";
            return $"[f=flv:onfail=abort]{EscapeTeeTarget(streamUrl)}";
        });
        arguments.Add(string.Join('|', targets));

        return startInfo;
    }

    private static string EscapeDeviceName(string name) =>
        name.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string EscapeTeeTarget(string target)
    {
        var escaped = new StringBuilder(target.Length);
        foreach (var character in target)
        {
            if (character is '\\' or '|' or '[' or ']' or '\'')
            {
                escaped.Append('\\');
            }

            escaped.Append(character);
        }

        return escaped.ToString();
    }
}
