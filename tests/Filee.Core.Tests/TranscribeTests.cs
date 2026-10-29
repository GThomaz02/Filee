// "Transcribe": Whisper's speech-to-text edges start a route only when the preset asks for them. A video → SRT stays
// "take the subtitle track" for every other preset, and the built-in presets sit on the audio and video donuts.

using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class TranscribeTests
{
    private static string Steps(Route? route) => string.Join(", ", route!.Steps.Select(s => $"{s.Converter.Id}:{s.From}→{s.To}"));

    private static RoutePlanner Planner() => new([
        new FakeConverter("whisper",
            new ConversionEdge("mp3", "txt", Transcribe: true), new ConversionEdge("mp3", "srt", Transcribe: true),
            new ConversionEdge("mp4", "txt", Transcribe: true), new ConversionEdge("mp4", "srt", Transcribe: true)),
        new FakeConverter("ffmpeg", new ConversionEdge("mp4", "srt"), new ConversionEdge("mp4", "mp3")),
        new FakeConverter("subtitles", new ConversionEdge("srt", "txt")),
    ]);

    [Fact]
    public void Transcribe_edges_are_never_part_of_an_ordinary_route()
    {
        var planner = Planner();

        Assert.Equal("ffmpeg:mp4→srt", Steps(planner.Plan("mp4", "srt")));     // the subtitle track, as before
        Assert.Equal("ffmpeg:mp4→srt, subtitles:srt→txt", Steps(planner.Plan("mp4", "txt")));
        Assert.Null(planner.Plan("mp3", "srt"));                               // audio has no subtitles to take
    }

    [Fact]
    public void Asking_to_transcribe_is_one_whisper_step()
    {
        var planner = Planner();

        Assert.Equal("whisper:mp4→srt", Steps(planner.Plan("mp4", "srt", transcribe: true)));
        Assert.Equal("whisper:mp4→txt", Steps(planner.Plan("mp4", "txt", transcribe: true)));
        Assert.Equal("whisper:mp3→srt", Steps(planner.Plan("mp3", "srt", transcribe: true)));
        Assert.Null(planner.Plan("mp3", "vtt", transcribe: true));
        Assert.True(planner.CanConvert("mp3", "txt", transcribe: true));
        Assert.False(planner.CanConvert("docx", "txt", transcribe: true));
    }

    [Theory]
    [InlineData("mp3", "txt", true)]
    [InlineData("wav", "srt", true)]
    [InlineData("mp4", "vtt", true)]
    [InlineData("mkv", "txt", true)]
    [InlineData("mp4", "mp3", false)]   // another target: converts as usual
    [InlineData("pdf", "txt", false)]   // not audio or video
    [InlineData("png", "srt", false)]
    public void A_preset_transcribes_audio_and_video_to_text_formats_only(string source, string target, bool expected)
    {
        var preset = new Preset { TargetFormat = target, Media = { Transcribe = true } };

        Assert.Equal(expected, preset.TranscribesOf(source));
        Assert.False(new Preset { TargetFormat = target }.TranscribesOf(source)); // not without the option
    }

    [Fact]
    public void The_built_in_presets_transcribe_with_a_default_model_and_detected_language()
    {
        var presets = BuiltInData.TranscribePresets();

        Assert.Equal(["transcribe-txt", "transcribe-srt", "transcribe-vtt"], presets.Select(p => p.Id));
        Assert.Equal(["txt", "srt", "vtt"], presets.Select(p => p.TargetFormat));
        Assert.All(presets, p =>
        {
            Assert.True(p.Media.Transcribe);
            Assert.Equal("small", p.Media.TranscribeModel);
            Assert.Equal("auto", p.Media.TranscribeLanguage);
        });
        Assert.All(presets, p => Assert.Single(BuiltInData.CreatePresets(), q => q.Id == p.Id));
    }

    [Fact]
    public void A_new_library_puts_transcribe_on_the_back_of_the_audio_and_video_donuts()
    {
        var profiles = BuiltInData.CreateProfiles();
        var audio = profiles.Single(p => p.Id == "audio").BackPresetIds!;
        var video = profiles.Single(p => p.Id == "video").BackPresetIds!;

        Assert.Contains("transcribe-txt", audio);
        Assert.Contains("transcribe-srt", audio);
        Assert.Contains("transcribe-vtt", audio);
        Assert.Contains("transcribe-txt", video);
        Assert.Contains("transcribe-srt", video);
        Assert.All(profiles, p => Assert.InRange(p.BackPresetIds!.Count, 0, ToolbarProfile.MaxSlices));
    }

    [Fact]
    public void A_schema_56_library_gains_transcribe_once_and_keeps_the_donuts_the_user_arranged()
    {
        var presets = BuiltInData.CreatePresets().Where(p => !p.Id.StartsWith("transcribe-")).ToList();
        var profiles = BuiltInData.CreateProfiles();
        // Schema 56's faces: no transcribe slices, the video back full.
        profiles.Single(p => p.Id == "audio").BackPresetIds = ["to-alac", "compress-audio", "merge-audio", "remove-metadata", "zip-all"];
        var video = profiles.Single(p => p.Id == "video");
        video.BackPresetIds = ["tool-clip", "mp4-av1", "to-webp", "extract-frames-png", "extract-frames-jpg", "live-photo-iphone",
            "motion-photo-android", "extract-audio", "to-wav", "compress-video", "merge-videos", "zip-all"];

        Assert.True(SettingsMigrations.ApplyToLibrary(56, presets, profiles));

        Assert.All(new[] { "transcribe-txt", "transcribe-srt", "transcribe-vtt" }, id => Assert.Single(presets, p => p.Id == id));
        Assert.Equal(["to-alac", "compress-audio", "merge-audio", "remove-metadata", "transcribe-txt", "transcribe-srt", "transcribe-vtt", "zip-all"],
            profiles.Single(p => p.Id == "audio").BackPresetIds);
        // The full video face gives up the second frame format and WAV (both stay in the library).
        Assert.Equal(ToolbarProfile.MaxSlices, video.BackPresetIds!.Count);
        Assert.Contains("transcribe-txt", video.BackPresetIds);
        Assert.Contains("transcribe-srt", video.BackPresetIds);
        Assert.DoesNotContain("extract-frames-jpg", video.BackPresetIds);
        Assert.DoesNotContain("to-wav", video.BackPresetIds);
        Assert.Single(presets, p => p.Id == "to-wav");
        Assert.False(SettingsMigrations.ApplyToLibrary(56, presets, profiles)); // once only
        Assert.False(SettingsMigrations.ApplyToLibrary(AppSettings.CurrentSchemaVersion, presets, profiles));
    }
}
