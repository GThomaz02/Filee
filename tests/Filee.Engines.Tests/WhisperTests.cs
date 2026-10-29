// Speech to text with whisper.cpp: the pinned downloads, and a real transcription of a spoken sentence. The real test
// needs Whisper and FFmpeg (pwsh build/fetch-engines.ps1 -Only ffmpeg,whisper) and downloads the "tiny" speech model the
// first time. The sentence is spoken by Windows' own voice, so it runs on Windows only.

using System.Diagnostics;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Infrastructure;
using Filee.Engines.Media;
using Filee.Engines.Speech;

namespace Filee.Engines.Tests;

public class WhisperListTests
{
    [Fact]
    public void Every_model_is_pinned()
    {
        Assert.Equal(["tiny", "base", "small", "medium"], WhisperModels.All.Select(m => m.Name));
        Assert.All(WhisperModels.All, model =>
        {
            Assert.Matches("^[0-9a-f]{64}$", model.Sha256);
            Assert.True(model.Size > 50_000_000);
        });
        Assert.Equal("small", WhisperModels.Default);
        Assert.NotNull(WhisperModels.Find(WhisperModels.Default));
        Assert.Null(WhisperModels.Find("gigantic"));
    }

    [Fact]
    public void Whisper_is_a_small_suggested_download_with_one_program()
    {
        var package = EngineDownloads.Packages.Single(p => p.Id == "whisper");

        Assert.Equal(["whisper"], package.ConverterIds);
        Assert.Equal(OperatingSystem.IsWindows() ? "whisper-cli.exe" : "whisper-cli", Assert.Single(EngineEnvironment.OwnCopyPrograms["whisper"]));
        if (OperatingSystem.IsWindows())
        {
            Assert.True(EngineDownloads.CanDownload(package));
            Assert.True(EngineDownloads.IsSuggested(package));
        }
    }

    [Fact]
    public void Its_edges_go_from_every_audio_and_video_format_to_txt_srt_and_vtt_only_when_asked()
    {
        var edges = new WhisperConverter().Edges;

        Assert.All(edges, e => Assert.True(e.Transcribe));
        Assert.Equal(["srt", "txt", "vtt"], edges.Select(e => e.To).Distinct().Order());
        Assert.Contains(edges, e => e.From == "mp3" && e.To == "txt");
        Assert.Contains(edges, e => e.From == "mp4" && e.To == "srt");
        Assert.DoesNotContain(edges, e => e.From == "pdf");
    }
}

public class WhisperTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly bool Installed = OperatingSystem.IsWindows() && WhisperConverter.Locate() is not null && FfmpegConverter.Locate() is not null;
    private const string Skip = "Whisper or FFmpeg is not installed (pwsh build/fetch-engines.ps1 -Only ffmpeg,whisper), or this is not Windows";

    /// <summary>A WAV of Windows' voice saying <paramref name="sentence"/> (System.Speech, through Windows PowerShell).</summary>
    private static string Speak(string folder, string sentence)
    {
        var path = Path.Combine(folder, "speech.wav");
        var script = "Add-Type -AssemblyName System.Speech; $s = New-Object System.Speech.Synthesis.SpeechSynthesizer; " +
                     $"$s.SetOutputToWaveFile('{path}'); $s.Speak('{sentence}'); $s.Dispose()";
        using var process = Process.Start(new ProcessStartInfo("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script])
            { CreateNoWindow = true, UseShellExecute = false })!;
        process.WaitForExit();
        Assert.True(File.Exists(path), "Windows' voice did not write the sentence.");
        return path;
    }

    private static Preset Transcribing(string target) =>
        new() { TargetFormat = target, Media = { Transcribe = true, TranscribeModel = "tiny", TranscribeLanguage = "en" } };

    [Fact]
    public async Task Speech_becomes_text()
    {
        Assert.SkipUnless(Installed, Skip);
        var wav = Speak(fx.NewFolder(), "Hello world. This is a test of the speech to text engine.");

        var job = await fx.ConvertAsync([wav], Transcribing("txt"));

        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var text = (await File.ReadAllTextAsync(job.Outputs.Single())).ToLowerInvariant();
        // The small "tiny" model mishears a robot voice a little ("hello ord"); the words it gets right are enough.
        Assert.Contains("hello", text);
        Assert.Contains("test", text);
    }

    [Fact]
    public async Task Speech_becomes_subtitles_with_times()
    {
        Assert.SkipUnless(Installed, Skip);
        var wav = Speak(fx.NewFolder(), "Hello world. This is a test of the speech to text engine.");

        var srt = await fx.ConvertAsync([wav], Transcribing("srt"));
        var vtt = await fx.ConvertAsync([wav], Transcribing("vtt"));

        Assert.True(srt.State == JobState.Completed && vtt.State == JobState.Completed);
        Assert.Matches(@"(?s)^1\s+\d\d:\d\d:\d\d,\d{3} --> \d\d:\d\d:\d\d,\d{3}\s+\S", await File.ReadAllTextAsync(srt.Outputs.Single()));
        Assert.StartsWith("WEBVTT", await File.ReadAllTextAsync(vtt.Outputs.Single()));
    }

    [Fact]
    public async Task The_sound_of_a_video_is_transcribed_too()
    {
        Assert.SkipUnless(Installed, Skip);
        var folder = fx.NewFolder();
        var wav = Speak(folder, "Hello world. This is a test of the speech to text engine.");
        var video = Path.Combine(folder, "talk.mp4");
        var (ffmpeg, _) = FfmpegConverter.Locate()!.Value;
        var made = await ProcessRunner.RunAsync(ffmpeg, ["-y", "-f", "lavfi", "-i", "color=c=black:s=160x120:r=10", "-i", wav, "-shortest",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", video], TimeSpan.FromMinutes(2), CancellationToken.None);
        Assert.True(made.ExitCode == 0, made.StandardError);

        var job = await fx.ConvertAsync([video], Transcribing("txt"));

        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        Assert.Contains("hello", (await File.ReadAllTextAsync(job.Outputs.Single())).ToLowerInvariant());
    }
}
