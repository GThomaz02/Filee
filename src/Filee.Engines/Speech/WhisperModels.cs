// The speech models Whisper (whisper.cpp) listens with, ggml format from the whisper.cpp model repository on Hugging
// Face (MIT), pinned by SHA-256 like the engines in engines.json. A model is downloaded the first time a preset needs
// it, checked, and kept in engines/whisper-models/<name>/ggml-<name>.bin by EngineInstaller, so a damaged or partial
// download never counts as installed. Larger models are more accurate and slower.

using System.Globalization;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Speech;

/// <summary>One Whisper model by its short name (<c>tiny</c>, <c>base</c>, <c>small</c>, <c>medium</c>).</summary>
public sealed record WhisperModel(string Name, string Sha256, long Size);

public static class WhisperModels
{
    /// <summary>Used when a preset doesn't say: a good balance of accuracy and speed on an ordinary PC.</summary>
    public const string Default = "small";

    private const string UrlFormat = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-{0}.bin";

    public static IReadOnlyList<WhisperModel> All { get; } =
    [
        new("tiny", "be07e048e1e599ad46341c8d2a135645097a538221678b7acdd1b1919c6e1b21", 77_691_713),
        new("base", "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe", 147_951_465),
        new("small", "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b", 487_601_967),
        new("medium", "6c14d5adee5f86394037b4e4e8b59f1673b6cee10e3cf0b11bbdbee79c156208", 1_533_763_059),
    ];

    /// <summary>Languages offered besides automatic detection: Whisper code and the culture for its name.</summary>
    public static IReadOnlyList<(string Code, string Culture)> Languages { get; } =
    [
        ("pt", "pt-BR"), ("en", "en"), ("es", "es"), ("fr", "fr"), ("de", "de"), ("it", "it"), ("ja", "ja"), ("ko", "ko"),
        ("zh", "zh-CN"), ("ru", "ru"), ("ar", "ar"), ("hi", "hi"), ("tr", "tr"), ("pl", "pl"),
    ];

    public static WhisperModel? Find(string? name) => All.FirstOrDefault(m => m.Name == name);

    /// <summary>Folder that receives one sub folder per model.</summary>
    public static string Root => Path.Combine(EngineEnvironment.DownloadRoot, "whisper-models");

    private static IReadOnlyDictionary<string, EngineComponent> Components { get; } = All.ToDictionary(
        m => m.Name,
        m => new EngineComponent(m.Name, "ggml", string.Format(CultureInfo.InvariantCulture, UrlFormat, m.Name), m.Sha256, m.Size, "ggml"));

    /// <summary>The model's file, or null while it isn't downloaded.</summary>
    public static string? FileOf(string name) =>
        EngineEnvironment.FirstExisting(Path.Combine(Root, name, $"ggml-{name}.bin")) is { } file
        && new EngineInstaller(Http.Value, Root, Components).IsInstalled(PackageOf(name))
            ? file : null;

    public static bool IsInstalled(string name) => FileOf(name) is not null;

    /// <summary>Downloads <paramref name="name"/> if it isn't there yet; one download at a time.</summary>
    public static async Task<string> EnsureAsync(string name, CancellationToken cancellationToken)
    {
        if (!Components.ContainsKey(name))
            throw new ArgumentException($"Whisper has no model '{name}' in Filee's list.", nameof(name));
        await OneAtATime.WaitAsync(cancellationToken);
        try
        {
            if (FileOf(name) is { } file)
                return file;
            await new EngineInstaller(Http.Value, Root, Components).InstallAsync(PackageOf(name), progress: null, cancellationToken);
            return FileOf(name) ?? throw new InvalidDataException($"The {name} model is missing after its download.");
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    /// <summary>Redirects are followed by the installer, which checks every file against its SHA-256.</summary>
    private static readonly Lazy<HttpClient> Http = new(() =>
    {
        var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Filee/{typeof(WhisperModels).Assembly.GetName().Version}");
        return http;
    });

    private static EnginePackage PackageOf(string name) => new("whisper-model-" + name, [name], Components[name].Size, ["whisper"]);
}
