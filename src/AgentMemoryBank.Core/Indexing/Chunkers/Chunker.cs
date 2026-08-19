using AgentMemoryBank.Models;

namespace AgentMemoryBank.Indexing.Chunkers;

/// <summary>
/// Phase A chunking: deterministic, no LLM (deterministic, no LLM).
/// Chunk by language — C++ on function and class boundaries, Python on def and
/// class, Markdown on headings, textproto on blocks — and fall back to fixed
/// line windows for anything else. Always correct, runs in seconds.
/// </summary>
public static class Chunker
{
    /// <summary>Longest span kept as a single chunk before it is split into windows.</summary>
    public const int MaxChunkLines = 220;

    private static readonly Dictionary<string, string> ExtensionLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cc"] = "cpp", ["cpp"] = "cpp", ["cxx"] = "cpp", ["c"] = "cpp",
        ["h"] = "cpp", ["hpp"] = "cpp", ["hxx"] = "cpp", ["cu"] = "cpp", ["cuh"] = "cpp", ["inc"] = "cpp",
        ["py"] = "python", ["pyi"] = "python",
        ["md"] = "markdown", ["markdown"] = "markdown",
        ["textproto"] = "textproto", ["pbtxt"] = "textproto", ["config"] = "textproto",
        ["proto"] = "proto",
        ["yaml"] = "yaml", ["yml"] = "yaml",
        ["sh"] = "shell", ["bash"] = "shell", ["ps1"] = "powershell",
        ["json"] = "json", ["toml"] = "toml", ["ini"] = "ini", ["cfg"] = "ini",
        ["txt"] = "text", ["tcss"] = "text", ["css"] = "text", ["sln"] = "text", ["bat"] = "text",
        ["cs"] = "csharp", ["csproj"] = "xml", ["xml"] = "xml",
    };

    private static readonly Dictionary<string, string> FileNameLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CMakeLists.txt"] = "text", ["Makefile"] = "text", ["Dockerfile"] = "text",
        [".clang-format"] = "yaml", [".gitignore"] = "text", [".gitattributes"] = "text",
        [".gitmodules"] = "ini",
    };

    /// <summary>Language tag for a repo-relative path, or "text" when nothing better fits.</summary>
    public static string LanguageFor(string path)
    {
        var name = Path.GetFileName(path);
        if (FileNameLanguages.TryGetValue(name, out var byName)) return byName;

        var dot = name.LastIndexOf('.');
        if (dot > 0 && ExtensionLanguages.TryGetValue(name[(dot + 1)..], out var byExtension)) return byExtension;
        return "text";
    }

    /// <summary>Normalise line endings and split, so line numbers mean the same on any platform.</summary>
    public static IReadOnlyList<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    /// <summary>
    /// Chunk one file's text. Never throws: a file the language chunker cannot
    /// make sense of falls back to plain line windows, which are still citable.
    /// </summary>
    public static IReadOnlyList<Chunk> ChunkText(string language, string text)
    {
        var lines = SplitLines(text);
        if (lines.All(ChunkCollector.IsBlank)) return [];

        IReadOnlyList<Chunk> chunks;
        try
        {
            chunks = language switch
            {
                "cpp" => CppChunker.Chunk(lines),
                "python" => PythonChunker.Chunk(lines),
                "markdown" => MarkdownChunker.Chunk(lines),
                "textproto" or "proto" or "json" => BracedChunker.Chunk(lines),
                "yaml" => YamlChunker.Chunk(lines),
                _ => [],
            };
        }
        catch
        {
            chunks = [];   // a malformed file must not fail the whole index
        }

        if (chunks.Count > 0) return chunks;

        var fallback = new ChunkCollector(lines);
        fallback.Emit(0, lines.Count - 1, "file");
        return fallback.Chunks;
    }
}
