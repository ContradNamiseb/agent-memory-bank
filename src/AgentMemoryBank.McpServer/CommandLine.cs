using System.Globalization;
using System.Numerics;
using AgentMemoryBank.Models;

namespace AgentMemoryBank.McpServer;

/// <summary>
/// Hand-rolled argument parsing. A parsing library would be one more dependency
/// to vet for a handful of flags, which is the trade this project keeps making.
/// </summary>
internal sealed record CommandLine
{
    private string? _db;
    private string? _root;

    public string Db { get => _db ?? Config.DbPath; private init => _db = value; }
    public string Root { get => _root ?? Config.RepoRoot; private init => _root = value; }
    public bool HasExplicitDb => _db is not null;
    public List<string> Repos { get; } = [];
    public string? Repo { get; private init; }
    public NoteKind? Kind { get; private init; }
    public int? LimitOption { get; private init; }
    public int Limit => LimitOption ?? 10;
    public string? Since { get; private init; }
    public bool WithGit { get; private init; }
    public bool Full { get; private init; }

    /// <summary>Skip the indexed-source section: notes only. What the recall hook uses.</summary>
    public bool NotesOnly { get; private init; }
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
    public string? ContextData { get; private init; }
    public string? Settings { get; private init; }
    public HandoffScope Scope { get; private init; } = HandoffScope.Repo;

    /// <summary>When <c>hook handoff</c> saves; force unless <c>--mode threshold</c>.</summary>
    public AutoHandoffMode HandoffMode { get; private init; } = AutoHandoffMode.Force;

    /// <summary>Context occupancy, in percent, at which threshold mode saves.</summary>
    public int Percent { get; private init; } = 95;

    /// <summary>Directory whose git checkout a handoff is recorded from or checked against.</summary>
    public string Cwd { get; private init; } = Environment.CurrentDirectory;

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
                case "--settings": options = options with { Settings = Next(arg) }; break;
                case "--root": options = options with { Root = Next(arg) }; break;
                case "--repo": options.Repos.Add(Next(arg)); break;
                case "--kind": options = options with { Kind = ParseKind(Next(arg)) }; break;
                case "--limit": options = options with { LimitOption = ParsePositive<int>(arg, Next(arg)) }; break;
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
                case "--context" or "--context-data": options = options with { ContextData = Next(arg) }; break;
                case "--confidence": options = options with { Confidence = ParseConfidence(Next(arg)) }; break;
                case "--scope": options = options with { Scope = ParseScope(Next(arg)) }; break;
                case "--cwd": options = options with { Cwd = Next(arg) }; break;
                case "--mode": options = options with { HandoffMode = ParseHandoffMode(Next(arg)) }; break;
                case "--percent": options = options with { Percent = ParsePercent(Next(arg)) }; break;
                case "--id": options = options with { Id = ParsePositive<long>(arg, Next(arg)) }; break;
                case "--tags":
                    options.Tags.AddRange(Next(arg).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                case "--full": options = options with { Full = true }; break;
                case "--notes-only": options = options with { NotesOnly = true }; break;
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

    private static T ParseNumber<T>(string flag, string value) where T : IBinaryInteger<T> =>
        T.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : throw new ArgumentException($"{flag} needs a whole number, got '{value}'");

    // A count or an id: 0 selects nothing, and SQLite reads a negative LIMIT as
    // no limit at all. Leave the option out for the default.
    private static T ParsePositive<T>(string flag, string value) where T : IBinaryInteger<T>
    {
        var number = ParseNumber<T>(flag, value);
        return number > T.Zero
            ? number
            : throw new ArgumentException($"{flag} must be 1 or more, got '{value}'");
    }

    // Below 1, threshold mode would save on every prompt; above 100, never.
    private static int ParsePercent(string value)
    {
        var percent = ParseNumber<int>("--percent", value);
        return percent is >= 1 and <= 100
            ? percent
            : throw new ArgumentException($"--percent must be between 1 and 100, got '{value}'");
    }

    private static Confidence ParseConfidence(string value) =>
        Enum.TryParse<Confidence>(value, ignoreCase: true, out var confidence)
            ? confidence
            : throw new ArgumentException(
                $"unknown confidence '{value}' (expected {string.Join(", ", Enum.GetNames<Confidence>()).ToLowerInvariant()})");

    private static HandoffScope ParseScope(string value) =>
        Enum.TryParse<HandoffScope>(value, ignoreCase: true, out var scope)
            ? scope
            : throw new ArgumentException(
                $"unknown scope '{value}' (expected {string.Join(", ", Enum.GetNames<HandoffScope>()).ToLowerInvariant()})");

    private static AutoHandoffMode ParseHandoffMode(string value) =>
        Enum.TryParse<AutoHandoffMode>(value, ignoreCase: true, out var mode)
            ? mode
            : throw new ArgumentException(
                $"unknown mode '{value}' (expected {string.Join(", ", Enum.GetNames<AutoHandoffMode>()).ToLowerInvariant()})");

    private static NoteKind ParseKind(string value) =>
        Enum.TryParse<NoteKind>(value, ignoreCase: true, out var kind)
            ? kind
            : throw new ArgumentException(
                $"unknown note kind '{value}' (expected {string.Join(", ", Enum.GetNames<NoteKind>()).ToLowerInvariant()})");
}
