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
    public bool Full { get; private init; }
    public bool Verbose { get; private init; }
    public string? Note { get; private init; }
    public string Query { get; private init; } = "";

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
                case "--note": options = options with { Note = Next(arg) }; break;
                case "--full": options = options with { Full = true }; break;
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

    private static NoteKind ParseKind(string value) =>
        Enum.TryParse<NoteKind>(value, ignoreCase: true, out var kind)
            ? kind
            : throw new ArgumentException(
                $"unknown note kind '{value}' (expected {string.Join(", ", Enum.GetNames<NoteKind>()).ToLowerInvariant()})");
}
