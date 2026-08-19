using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentMemoryBank;

/// <summary>
/// Which repositories are indexed, where they live, and what is skipped.
///
/// <para>Nothing here names a real project. Settings come from a local file
/// (<c>memory-bank.local.json</c>, which is never tracked) or from environment
/// variables, so a checkout carries no trace of what it was pointed at. That
/// matters on a shared or public remote: the index is about someone's private
/// source tree even when the tool is not.</para>
///
/// <para>Precedence: environment variable, then the local settings file, then
/// a harmless built-in default.</para>
/// </summary>
public static class Config
{
    /// <summary>Name of the untracked settings file, looked for at the checkout root.</summary>
    public const string SettingsFileName = "memory-bank.local.json";

    /// <summary>Directory holding the repositories named in <see cref="Repos"/>.</summary>
    public static string RepoRoot =>
        Environment.GetEnvironmentVariable("MEMORY_BANK_REPO_ROOT")
        ?? Settings.RepoRoot
        ?? Environment.CurrentDirectory;

    /// <summary>
    /// Repository directory names, relative to <see cref="RepoRoot"/>. Empty
    /// until configured — the tool indexes nothing it was not pointed at.
    /// </summary>
    public static IReadOnlyList<string> Repos
    {
        get
        {
            var fromEnvironment = Environment.GetEnvironmentVariable("MEMORY_BANK_REPOS");
            if (!string.IsNullOrWhiteSpace(fromEnvironment))
                return fromEnvironment.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return Settings.Repos ?? [];
        }
    }

    /// <summary>Path of the SQLite file. Untracked, like the settings it was built from.</summary>
    public static string DbPath =>
        Environment.GetEnvironmentVariable("MEMORY_BANK_DB")
        ?? Settings.DbPath
        ?? Path.Combine(ProjectRoot, "memory.db");

    /// <summary>
    /// Directory names skipped wherever they appear as the first path segment.
    /// Vendored third-party trees are noise: they are someone else's code, they
    /// dwarf the repository that contains them, and nothing in them is a finding.
    /// </summary>
    public static IReadOnlySet<string> VendoredDirs =>
        new HashSet<string>(
            Settings.VendoredDirs ?? ["vendor", "third_party", "external", "libs", "subprojects", "node_modules"],
            StringComparer.OrdinalIgnoreCase);

    /// <summary>Files larger than this are not read at all.</summary>
    public static long MaxFileBytes => Settings.MaxFileBytes ?? 2 * 1024 * 1024;

    /// <summary>
    /// Extensions never worth reading as text. Project-specific formats belong
    /// in "extraBinaryExtensions" in the local settings, not in this list: what
    /// file types a codebase contains is itself a detail about that codebase.
    /// </summary>
    public static IReadOnlySet<string> BinaryExtensions =>
        Settings.ExtraBinaryExtensions is null or []
            ? DefaultBinaryExtensions
            : new HashSet<string>(
                DefaultBinaryExtensions.Concat(Settings.ExtraBinaryExtensions),
                StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlySet<string> DefaultBinaryExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "pdf", "png", "jpg", "jpeg", "gif", "ico", "bmp", "webp", "svgz",
            "gz", "zip", "xz", "bz2", "7z", "tar", "bin", "exe", "dll", "so", "dylib",
            "o", "a", "lib", "pyc", "pyd", "npz", "npy", "pb", "onnx", "pt", "pth",
            "ttf", "otf", "woff", "woff2", "mp4", "mp3", "wav", "mkv", "iso",
        };

    /// <summary>True when nothing has told the tool what to index yet.</summary>
    public static bool IsConfigured => Repos.Count > 0;

    /// <summary>Message shown when the tool is run before it has been pointed at anything.</summary>
    public static string ConfigurationHelp =>
        // Two '$' so a single brace is literal JSON and only {{...}} interpolates.
        $$"""
        No repositories are configured, so there is nothing to index.

        Create {{SettingsFileName}} at {{ProjectRoot}} (it is gitignored):

        {
          "repoRoot": "C:/path/to/your/repos",
          "repos": ["first-repo", "second-repo"]
        }

        Or set MEMORY_BANK_REPO_ROOT and MEMORY_BANK_REPOS for a single run.
        See {{SettingsFileName}}.example for every available setting.
        """;

    /// <summary>
    /// Markers identifying the checkout root. Both solution formats are listed:
    /// .NET 10 writes the XML .slnx by default, and matching only ".sln" made
    /// this walk fail silently — the default database path then resolved against
    /// whatever directory the tool happened to be launched from.
    /// </summary>
    private static readonly string[] RootMarkers = ["AgentMemoryBank.slnx", "AgentMemoryBank.sln"];

    /// <summary>
    /// The agent-memory-bank checkout itself. Walks up from the running assembly
    /// so the default database path is the same whether the tool was launched by
    /// `dotnet run` from source or as a published executable in another directory.
    /// </summary>
    public static string ProjectRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                if (RootMarkers.Any(marker => File.Exists(Path.Combine(dir.FullName, marker))))
                    return dir.FullName;
                dir = dir.Parent;
            }
            return Environment.CurrentDirectory;
        }
    }

    private static LocalSettings? _settings;

    /// <summary>Settings from the local file, read once, empty when absent.</summary>
    public static LocalSettings Settings => _settings ??= LoadSettings();

    /// <summary>Re-read the settings file. For tests, and after editing it by hand.</summary>
    public static void ReloadSettings() => _settings = null;

    private static LocalSettings LoadSettings()
    {
        var path = Environment.GetEnvironmentVariable("MEMORY_BANK_SETTINGS")
            ?? Path.Combine(ProjectRoot, SettingsFileName);
        if (!File.Exists(path)) return new LocalSettings();

        try
        {
            return JsonSerializer.Deserialize<LocalSettings>(File.ReadAllText(path), SettingsJson)
                ?? new LocalSettings();
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException($"{path} is not valid JSON: {e.Message}");
        }
    }

    private static readonly JsonSerializerOptions SettingsJson = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Resolve the on-disk directory for a repository name.
    /// Uses repoPaths mapping from settings if specified, otherwise combines with repoRoot.
    /// </summary>
    public static string GetRepoDirectory(string repoName, string? repoRoot = null)
    {
        repoRoot ??= RepoRoot;
        if (Settings.RepoPaths is not null && Settings.RepoPaths.TryGetValue(repoName, out var customPath))
        {
            return Path.IsPathRooted(customPath) ? customPath : Path.Combine(repoRoot, customPath);
        }
        return Path.IsPathRooted(repoName) ? repoName : Path.Combine(repoRoot, repoName);
    }
}

/// <summary>Shape of <c>memory-bank.local.json</c>. Every field is optional.</summary>
public sealed record LocalSettings
{
    [JsonPropertyName("repoRoot")]
    public string? RepoRoot { get; init; }

    [JsonPropertyName("repos")]
    public string[]? Repos { get; init; }

    [JsonPropertyName("repoPaths")]
    public Dictionary<string, string>? RepoPaths { get; init; }

    [JsonPropertyName("dbPath")]
    public string? DbPath { get; init; }

    [JsonPropertyName("vendoredDirs")]
    public string[]? VendoredDirs { get; init; }

    [JsonPropertyName("extraBinaryExtensions")]
    public string[]? ExtraBinaryExtensions { get; init; }

    [JsonPropertyName("maxFileBytes")]
    public long? MaxFileBytes { get; init; }
}
