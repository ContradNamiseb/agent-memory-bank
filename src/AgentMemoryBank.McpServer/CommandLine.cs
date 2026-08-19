using AgentMemoryBank.Models;

namespace AgentMemoryBank.McpServer;

/// <summary>
/// Hand-rolled argument parsing. A parsing library would be one more dependency
/// to vet for a handful of flags, which is the trade this project keeps making.
/// </summary>
internal sealed record CommandLine
{
    public string Db { get; private init; } = Config.DbPath;
    public string Root { get; private init; } = Config.RepoRoot;
    public List<string> Repos { get; } = [];
    public string? Repo { get; private init; }
    public NoteKind? Kind { get; private init; }
    public int Limit { get; private init; } = 10;
    public string? Since { get; private init; }
    public bool WithGit { get; private init; }
    public bool Full { get; private init; }
    public bool Verbose { get; private init; }
    public string? Note { get; private init; }
    public string Query { get; private init; } = "";
    public string? Claim { get; private init; }
    public string? Evidence { get; private init; }
    public string? Why { get; private init; }
    public Confidence? Confidence { get; private init; }
    public List<string> Tags { get; } = [];
    public long? Id { get; private init; }
    public string? Agent { get; private init; }
    public string? Topic { get; private init; }
    public string? Summary { get; private init; }
    public string? NextSteps { get; private init; }
    public string? ActiveFiles { get; private init; }
    public string? OpenQuestions { get; private init; }

    public static CommandLine Parse(string[] args)
    {
        var options = new CommandLine();
        var words = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string Next(string flag) => i + 1 < args.Length
                ? args[++i]
                : throw new ArgumentException($"{flag} needs a value");

            switch (arg)
            {
                case "--db": options = options with { Db = Next(arg) }; break;
                case "--root": options = options with { Root = Next(arg) }; break;
                case "--repo": options.Repos.Add(Next(arg)); break;
                case "--kind": options = options with { Kind = ParseKind(Next(arg)) }; break;
                case "--limit": options = options with { Limit = int.Parse(Next(arg)) }; break;
                case "--since": options = options with { Since = Next(arg) }; break;
                case "--note": options = options with { Note = Next(arg) }; break;
                case "--claim": options = options with { Claim = Next(arg) }; break;
                case "--evidence": options = options with { Evidence = Next(arg) }; break;
                case "--why": options = options with { Why = Next(arg) }; break;
                case "--agent": options = options with { Agent = Next(arg) }; break;
                case "--topic": options = options with { Topic = Next(arg) }; break;
                case "--summary": options = options with { Summary = Next(arg) }; break;
                case "--next" or "--next-steps": options = options with { NextSteps = Next(arg) }; break;
                case "--files" or "--active-files": options = options with { ActiveFiles = Next(arg) }; break;
                case "--questions" or "--open-questions": options = options with { OpenQuestions = Next(arg) }; break;
                case "--confidence": options = options with { Confidence = ParseConfidence(Next(arg)) }; break;
                case "--id": options = options with { Id = long.Parse(Next(arg)) }; break;
                case "--tags":
                    options.Tags.AddRange(Next(arg).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                case "--full": options = options with { Full = true }; break;
                case "--with-git": options = options with { WithGit = true }; break;
                case "-v" or "--verbose": options = options with { Verbose = true }; break;
                default:
                    if (arg.StartsWith('-')) throw new ArgumentException($"unknown option '{arg}'");
                    words.Add(arg);
                    break;
            }
        }

        // For search, --repo filters to one repository; for index it may repeat.
        return options with
        {
            Query = string.Join(' ', words),
            Repo = options.Repos.Count == 1 ? options.Repos[0] : null,
        };
    }

    private static Confidence ParseConfidence(string value) =>
        Enum.TryParse<Confidence>(value, ignoreCase: true, out var confidence)
            ? confidence
            : throw new ArgumentException(
                $"unknown confidence '{value}' (expected {string.Join(", ", Enum.GetNames<Confidence>()).ToLowerInvariant()})");

    private static NoteKind ParseKind(string value) =>
        Enum.TryParse<NoteKind>(value, ignoreCase: true, out var kind)
            ? kind
            : throw new ArgumentException(
                $"unknown note kind '{value}' (expected {string.Join(", ", Enum.GetNames<NoteKind>()).ToLowerInvariant()})");
}
