// Speech to text with whisper.cpp (MIT, a separate program, bundled with the installer or downloaded into
// engines/whisper): any audio or video file → TXT, SRT or WebVTT, in the language the preset names or detected by
// Whisper. FFmpeg first turns the sound into the 16 kHz mono WAV Whisper reads (video is never decoded further than
// its audio track); whisper-cli then listens with the model of WhisperModels, downloaded the first time it is used.
// Runs entirely on this PC: nothing is uploaded.
//
// The edges are ConversionEdge.Transcribe ones: taken only when a preset asks for it (Preset.TranscribesOf), so a
// video → SRT stays "take the subtitle track" for every other preset.

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Engines.Infrastructure;
using Filee.Engines.Media;

namespace Filee.Engines.Speech;

/// <summary>Audio and video → TXT, SRT, WebVTT through whisper.cpp.</summary>
public sealed partial class WhisperConverter : IConverter
{
    /// <summary>A recording of hours legitimately takes a long time on a PC without a graphics chip.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromHours(8);

    /// <summary>Whisper reports nothing for a long time while it loads a large model or works on a dense passage.</summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromMinutes(20);

    private string? _executable;

    public string Id => "whisper";
    public string DisplayName => "Whisper";

    /// <summary>Whisper uses every core by itself: one file at a time.</summary>
    public int MaxParallelism => 1;

    public IReadOnlyList<ConversionEdge> Edges { get; } =
    [
        .. from source in FormatRegistry.Known.Where(f => f.Category is FormatCategory.Audio or FormatCategory.Video)
           from target in MediaOptions.TranscribeTargets
           select new ConversionEdge(source.Id, target, Transcribe: true),
    ];

    public EngineStatus GetStatus()
    {
        _executable = Locate();
        if (_executable is null)
            return EngineStatus.Unavailable("engine.reason.not_installed");
        if (FfmpegConverter.Locate() is null)
            return EngineStatus.Unavailable("engine.reason.not_installed", "FFmpeg");
        return EngineStatus.Available(_executable, EngineEnvironment.UsesFileesCopy("whisper") ? EngineVersions.Component("whisper") : null);
    }

    /// <summary>The user's chosen copy, Filee's own, then one on the PC.</summary>
    internal static string? Locate() =>
        EngineEnvironment.OwnProgram("whisper", "whisper-cli.exe") ?? EngineEnvironment.BundledProgram("whisper", "whisper-cli.exe")
        ?? EngineEnvironment.SystemProgram("whisper", "whisper-cli.exe");

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var whisper = _executable ?? Locate() ?? throw new InvalidOperationException("Whisper is not installed.");
        var (ffmpeg, _) = FfmpegConverter.Locate() ?? throw new InvalidOperationException("FFmpeg was not found.");
        var output = step.Output.Allocate(step.To);
        if (output is null)
            return [];

        var media = step.Preset.Media;
        var model = WhisperModels.Find(media.TranscribeModel) ?? WhisperModels.Find(WhisperModels.Default)!;
        string modelFile;
        try
        {
            modelFile = await WhisperModels.EnsureAsync(model.Name, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new ConversionRefusedException("error.whisper_model_download", $"{model.Name}: {ex.Message}");
        }
        progress?.Report(0.02);

        // Plain relative names in the job's folder: Whisper opens files through the ANSI code page on some Windows builds.
        var scratch = Path.Combine(step.WorkDirectory, "whisper-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(scratch);
        var audio = Path.Combine(scratch, "audio.wav");
        var extract = await FfmpegRunner.RunAsync(ffmpeg,
            ["-nostdin", "-y", "-i", step.InputPath, "-map", "0:a:0", "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", "-progress", "pipe:1", audio],
            onPosition: null, cancellationToken);
        if (extract.ExitCode != 0 || !File.Exists(audio))
        {
            if (extract.StandardError.Contains("matches no streams", StringComparison.Ordinal))
                throw new ConversionRefusedException("error.no_audio_track");
            throw new InvalidOperationException($"FFmpeg could not read the sound (exit {extract.ExitCode}). {Tail(extract.StandardError)}");
        }
        progress?.Report(0.1);

        var language = string.IsNullOrWhiteSpace(media.TranscribeLanguage) ? "auto" : media.TranscribeLanguage.Trim();
        List<string> args =
        [
            "-m", ShortPath(modelFile), "-f", "audio.wav", "-l", language, "-of", "transcript", "-pp",
            step.To switch { "srt" => "-osrt", "vtt" => "-ovtt", _ => "-otxt" },
        ];
        var result = await ProcessRunner.RunAsync(whisper, args, Timeout, cancellationToken, workingDirectory: scratch,
            onOutput: line =>
            {
                if (Percent().Match(line) is { Success: true } match)
                    progress?.Report(0.1 + 0.88 * int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) / 100.0);
            },
            stallTimeout: StallTimeout);
        var written = Path.Combine(scratch, "transcript." + step.To);
        if (result.ExitCode != 0 || !File.Exists(written))
            throw new InvalidOperationException($"Whisper failed (exit {result.ExitCode}). {Tail(result.StandardError)}");

        File.Move(written, output, overwrite: true);
        progress?.Report(1);
        return [output];
    }

    /// <summary>The "progress =  42%" lines that <c>-pp</c> writes.</summary>
    [GeneratedRegex(@"progress\s*=\s*(\d{1,3})\s*%")]
    private static partial Regex Percent();

    private static string Tail(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" ", lines.TakeLast(3));
    }

    /// <summary>
    /// The model sits under the user's profile folder, whose name may have letters outside ASCII; the 8.3 name of the
    /// file avoids the ANSI code page problem without copying half a gigabyte.
    /// </summary>
    private static string ShortPath(string path)
    {
        if (!OperatingSystem.IsWindows() || path.All(c => c < 128))
            return path;
        var buffer = new StringBuilder(520);
        return GetShortPathName(path, buffer, buffer.Capacity) is > 0 and < 520 ? buffer.ToString() : path;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetShortPathNameW")]
    private static extern int GetShortPathName(string longPath, StringBuilder shortPath, int bufferSize);
}
