using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentMemoryBank.McpServer;

// Kept identical to AgoraMCP's copy apart from this namespace: the two servers
// share no library, and the guard is driven by each tool's own schema.

/// <summary>
/// Sits between the client and every tool: repairs the argument mistakes a
/// model actually makes, and turns the rest into errors it can act on.
///
/// <para>Without it, a misnamed argument is silently dropped by the SDK's
/// binder and resurfaces as an unrelated refusal ("needs a non-empty body"),
/// and a bad enum value fails inside the binder as the opaque "An error
/// occurred invoking 'x'". Both cost the caller a guess and a retry. Here a
/// known alias (message→body, threadId→thread_id, author→actor) is mapped to
/// the parameter the tool declares, "#12" becomes 12, an enum value is matched
/// case-insensitively (or by a unique prefix), and anything still wrong comes
/// back with the tool's full signature so the next call can be right.</para>
///
/// <para>Driven entirely by the tool's own input schema, so it needs no
/// per-tool code and cannot drift from what tools/list advertises.</para>
/// </summary>
internal static partial class ToolArgumentGuard
{
    /// <summary>Every name a model uses for "me". A tool declares at most one of the canonical ones.</summary>
    private static readonly HashSet<string> Identity =
    [
        "author", "actor", "agent", "from_agent", "filed_by",
        "me", "self", "agent_name", "my_name", "sender", "from", "caller", "reviewer",
        "implementer", "claimant", "poster", "agent_id", "name",
    ];

    /// <summary>Canonical parameter → names models reach for instead. Applied only when the tool declares the canonical name.</summary>
    private static readonly Dictionary<string, string[]> Aliases = new()
    {
        ["body"] = ["message", "text", "content", "msg", "review", "reply", "comment", "critique", "response"],
        ["thread_id"] = ["thread", "tid", "thread_number", "thread_no", "id"],
        ["handoff_id"] = ["handoff", "id"],
        ["topic"] = ["title", "subject", "question", "headline"],
        ["context"] = ["details", "detail", "description", "background", "body"],
        ["since_id"] = ["since", "last_seen_message_id", "last_seen_id", "last_seen", "after_id", "after",
                        "since_message_id", "cursor", "from_id"],
        ["limit"] = ["max", "max_results", "count", "n", "top", "max_items"],
        ["max_chars"] = ["max_length", "char_limit", "budget", "max_size", "chars", "limit_chars"],
        ["specialty"] = ["tag", "domain", "area", "skill"],
        ["instruction"] = ["instructions", "task", "action", "ask"],
        ["to_agent"] = ["to", "recipient", "target", "target_agent", "receiver", "assignee", "next_agent", "pass_to"],
        ["target_agent"] = ["to_agent", "to", "recipient", "target", "directed_to", "assignee"],
        ["pass_to"] = ["to_agent", "to", "next_agent", "recipient", "target", "assignee"],
        ["revision"] = ["rev", "commit", "sha", "hash", "manifest", "version", "snapshot_id"],
        ["verdict"] = ["decision", "outcome", "result", "review_verdict"],
        ["next_action"] = ["next_step", "next_steps", "next", "todo"],
        ["covered_through_msg_id"] = ["covered_through", "through_msg_id", "last_msg_id", "up_to",
                                      "through_id", "covered_through_message_id", "msg_id"],
        ["query"] = ["q", "search", "keywords", "terms", "text"],
        ["session_id"] = ["session", "id", "chat_id"],
        ["question"] = ["q", "memo_question", "query"],
        ["answer"] = ["response", "solution", "result"],
        ["capabilities"] = ["caps", "blurb", "about", "model"],
        ["status"] = ["state", "thread_status", "new_status"],
        ["kind"] = ["type", "message_kind", "category"],
        ["timeout_seconds"] = ["timeout", "wait_seconds", "timeout_s"],
        ["title_filter"] = ["filter", "title"],
        ["per_client"] = ["limit", "count"],
        ["last"] = ["limit", "turns", "max_turns", "tail"],
        ["recent_limit"] = ["limit", "recent", "last"],
        ["digest"] = ["use_digest"],
        ["max_age_days"] = ["max_age", "age_days"],
        ["since_days"] = ["days", "window_days"],
        ["stage"] = ["workflow_stage", "phase"],
        ["source_thread_id"] = ["thread_id", "thread"],
        ["source_msg_id"] = ["msg_id", "message_id"],
        ["tokens"] = ["amount", "n_tokens", "token_count", "count"],
        ["slot"] = ["workstream"],
        // agent-memory-bank's parameters; the table is shared verbatim by both servers.
        ["claim"] = ["statement", "fact", "finding", "assertion"],
        ["evidence"] = ["proof", "citation", "citations", "refs", "source", "sources"],
        ["rationale"] = ["why", "reason", "reasoning", "explanation"],
        ["tags"] = ["tag", "labels", "label"],
        ["ids"] = ["id_list", "note_ids", "chunk_ids", "id"],
        ["repo"] = ["repository", "repo_name", "project"],
        ["supersedes"] = ["replaces", "supersede", "replace", "superseded_ids"],
        ["claims"] = ["claim", "statements"],
        ["kinds"] = ["kind"],
        ["confidences"] = ["confidence"],
        ["rationales"] = ["rationale", "why"],
        ["next_steps"] = ["next_action", "next", "todo"],
        ["summary"] = ["recap", "description"],
        ["active_files"] = ["files", "modified_files"],
        ["open_questions"] = ["questions", "blockers"],
        ["dry_run"] = ["dryrun", "preview", "validate_only"],
        ["include_superseded"] = ["superseded", "all"],
        ["before_id"] = ["before", "cursor"],
    };

    public static IMcpServerBuilder WithArgumentGuard(this IMcpServerBuilder builder) =>
        builder.WithRequestFilters(filters =>
        {
            // Raw-string descriptions carry the source file's line breaks (CRLF
            // in some files), which is pure overhead in a schema every session
            // loads. One space per break keeps the words and drops the bytes.
            filters.AddListToolsFilter(next => async (context, cancellationToken) =>
            {
                var result = await next(context, cancellationToken);
                foreach (var tool in result.Tools)
                {
                    if (tool.Description is { } d) tool.Description = Compact(d);
                    tool.InputSchema = Slim(tool.InputSchema);
                }
                return result;
            });

            filters.AddCallToolFilter(next => async (context, cancellationToken) =>
            {
                if (context.MatchedPrimitive is not McpServerTool tool || context.Params is null)
                    return await next(context, cancellationToken);

                var name = tool.ProtocolTool.Name;
                var outcome = Normalize(name, tool.ProtocolTool.InputSchema, context.Params.Arguments);
                if (outcome.Error is not null) return Text(outcome.Error, isError: true);
                context.Params.Arguments = outcome.Arguments;

                CallToolResult result;
                try
                {
                    result = await next(context, cancellationToken);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    return Text($"Error: {name}: {e.GetType().Name}: {e.Message}. " +
                                $"Signature: {Signature(name, tool.ProtocolTool.InputSchema)}", isError: true);
                }
                return Annotate(result, outcome.Notes);
            });
        });

    public sealed record Outcome(IDictionary<string, JsonElement> Arguments, string? Error, IReadOnlyList<string> Notes);

    /// <summary>Map, coerce and check the arguments against the schema. Pure, so it is tested directly.</summary>
    public static Outcome Normalize(string tool, JsonElement schema, IDictionary<string, JsonElement>? arguments)
    {
        var props = schema.ValueKind == JsonValueKind.Object &&
                    schema.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object
            ? p.EnumerateObject().ToDictionary(x => x.Name, x => x.Value)
            : [];
        var required = schema.ValueKind == JsonValueKind.Object &&
                       schema.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.Array
            ? r.EnumerateArray().Select(x => x.GetString() ?? "").ToHashSet()
            : [];

        var input = arguments ?? new Dictionary<string, JsonElement>();
        var output = new Dictionary<string, JsonElement>();
        var notes = new List<string>();
        var problems = new List<string>();
        var unknown = new List<string>();

        // An explicit null on an optional parameter means "not given": some
        // clients send every field, and the slimmed schema no longer lists null.
        // So does "" for an optional enum or number, which cannot mean anything else.
        input = input.Where(kv => required.Contains(kv.Key) || !Omitted(kv.Value, props.GetValueOrDefault(kv.Key)))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        foreach (var (key, value) in input)
            if (props.ContainsKey(key)) output[key] = value;

        foreach (var (key, value) in input)
        {
            if (props.ContainsKey(key)) continue;
            var target = Resolve(key, props.Keys, output.Keys);
            if (target is null)
            {
                unknown.Add(key);
                continue;
            }
            output[target] = value;
            notes.Add($"read '{key}' as '{target}'");
        }

        foreach (var key in output.Keys.ToList())
        {
            var (value, problem, note) = Coerce(key, props[key], output[key]);
            if (problem is not null) problems.Add(problem);
            else output[key] = value;
            if (note is not null) notes.Add(note);
        }

        var missing = required.Where(k => !output.TryGetValue(k, out var v) || v.ValueKind == JsonValueKind.Null)
            .ToList();
        if (missing.Count > 0)
            problems.Add($"missing required {string.Join(", ", missing)}");
        if (unknown.Count > 0 && problems.Count > 0)
            problems.Add($"unknown {string.Join(", ", unknown.Select(u => $"'{u}'"))}");

        if (problems.Count > 0)
            return new Outcome(output, $"Error: {tool}: {string.Join("; ", problems)}. " +
                                       $"Signature: {Signature(tool, schema)}", notes);

        if (unknown.Count > 0)
            notes.Add($"ignored unknown {string.Join(", ", unknown.Select(u => $"'{u}'"))}; " +
                      $"signature: {Signature(tool, schema)}");
        return new Outcome(output, null, notes);
    }

    private static bool Omitted(JsonElement value, JsonElement prop)
    {
        if (value.ValueKind == JsonValueKind.Null) return true;
        if (value.ValueKind != JsonValueKind.String || value.GetString()!.Trim().Length > 0) return false;
        if (prop.ValueKind != JsonValueKind.Object) return false;
        return prop.TryGetProperty("enum", out _) || !Types(prop).Contains("string");
    }

    /// <summary>The declared parameter a supplied name most plausibly meant, or null.</summary>
    private static string? Resolve(string key, IEnumerable<string> declared, IEnumerable<string> supplied)
    {
        var free = declared.Except(supplied).ToList();
        var snake = Snake(key);
        if (free.Contains(snake)) return snake;

        var candidates = free.Where(c => Aliases.TryGetValue(c, out var a) && a.Contains(snake)).ToList();
        if (candidates.Count == 1) return candidates[0];
        if (candidates.Count > 1) return null;

        if (Identity.Contains(snake))
        {
            var mine = free.Where(Identity.Contains).ToList();
            if (mine.Count == 1) return mine[0];
        }
        return null;
    }

    private static (JsonElement Value, string? Problem, string? Note) Coerce(string name, JsonElement prop, JsonElement value)
    {
        var types = Types(prop);

        if (prop.TryGetProperty("enum", out var choices))
            return MatchEnum(name, choices, value);

        if (types.Contains("array") && prop.TryGetProperty("items", out var items))
        {
            // A lone string is one element of a text list ("a claim with spaces"),
            // but a delimited list of ids ("3, #4, 5") for anything else.
            var textItems = Types(items).Contains("string") && !items.TryGetProperty("enum", out _);
            var elements = value.ValueKind switch
            {
                JsonValueKind.Array => value.EnumerateArray().ToList(),
                JsonValueKind.String when textItems => [value],
                JsonValueKind.String => value.GetString()!
                    .Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => JsonSerializer.SerializeToElement(s)).ToList(),
                JsonValueKind.Null => null,
                _ => [value],
            };
            if (elements is null) return (value, null, null);
            var coerced = new List<JsonElement>();
            foreach (var element in elements)
            {
                var (v, problem, _) = Coerce(name, items, element);
                if (problem is not null) return (value, problem, null);
                coerced.Add(v);
            }
            return (JsonSerializer.SerializeToElement(coerced), null, null);
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!.Trim();
            if (types.Contains("integer") && !types.Contains("string"))
            {
                if (long.TryParse(text.TrimStart('#'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                    return (JsonSerializer.SerializeToElement(n), null, null);
                if (text.Length == 0 && types.Contains("null")) return (JsonSerializer.SerializeToElement<long?>(null), null, null);
                return (value, $"{name}='{text}' is not an integer", null);
            }
            if (types.Contains("number") && !types.Contains("string"))
            {
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    return (JsonSerializer.SerializeToElement(d), null, null);
                return (value, $"{name}='{text}' is not a number", null);
            }
            if (types.Contains("boolean") && !types.Contains("string"))
            {
                var b = text.ToLowerInvariant() switch
                {
                    "true" or "1" or "yes" or "y" or "on" => true,
                    "false" or "0" or "no" or "n" or "off" or "" => (bool?)false,
                    _ => null,
                };
                return b is { } flag
                    ? (JsonSerializer.SerializeToElement(flag), null, null)
                    : (value, $"{name}='{text}' is not a boolean (true or false)", null);
            }
        }

        // A number or boolean where text is declared: the model meant the literal.
        if (types.Contains("string") && value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            return (JsonSerializer.SerializeToElement(value.GetRawText()), null, null);

        return (value, null, null);
    }

    private static (JsonElement Value, string? Problem, string? Note) MatchEnum(string name, JsonElement choices, JsonElement value)
    {
        var allowed = choices.EnumerateArray()
            .Where(c => c.ValueKind == JsonValueKind.String)
            .Select(c => c.GetString()!)
            .ToList();
        var nullable = choices.EnumerateArray().Any(c => c.ValueKind == JsonValueKind.Null);

        if (value.ValueKind == JsonValueKind.Null)
            return nullable ? (value, null, null) : (value, $"{name} is required; use one of: {string.Join(", ", allowed)}", null);

        var given = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
        var key = EnumKey(given);
        if (key.Length == 0 && nullable) return (JsonSerializer.SerializeToElement<string?>(null), null, null);

        var exact = allowed.Where(a => EnumKey(a) == key).ToList();
        if (exact.Count == 1)
            return (JsonSerializer.SerializeToElement(exact[0]), null, null);

        // A unique prefix in either direction: 'approve' → 'approved', 'changes' → 'changes_required'.
        var loose = allowed.Where(a => key.Length >= 3 &&
                                       (EnumKey(a).StartsWith(key, StringComparison.Ordinal) ||
                                        key.StartsWith(EnumKey(a), StringComparison.Ordinal))).ToList();
        if (loose.Count == 1)
            return (JsonSerializer.SerializeToElement(loose[0]), null, $"read {name}='{given}' as '{loose[0]}'");

        return (value, $"{name}='{given}' is not valid; use one of: {string.Join(", ", allowed)}", null);
    }

    /// <summary>"pass_turn(thread_id: integer!, to_agent: string!, stage?: in_progress|dev_tested|…)".</summary>
    public static string Signature(string tool, JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object)
            return $"{tool}()";
        var required = schema.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.Array
            ? r.EnumerateArray().Select(x => x.GetString()).ToHashSet()
            : [];
        var parts = props.EnumerateObject().Select(p =>
        {
            string type;
            if (p.Value.TryGetProperty("enum", out var e))
                type = string.Join('|', e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()));
            else
            {
                type = string.Join('|', Types(p.Value).Where(t => t != "null"));
                if (type == "array" && p.Value.TryGetProperty("items", out var items))
                    type = $"{string.Join('|', Types(items))}[]";
            }
            return required.Contains(p.Name) ? $"{p.Name}: {type}!" : $"{p.Name}?: {type}";
        });
        return $"{tool}({string.Join(", ", parts)})";
    }

    private static HashSet<string> Types(JsonElement prop)
    {
        if (!prop.TryGetProperty("type", out var t)) return [];
        return t.ValueKind == JsonValueKind.Array
            ? t.EnumerateArray().Select(x => x.GetString() ?? "").ToHashSet()
            : [t.GetString() ?? ""];
    }

    private static string Snake(string name) =>
        CamelBoundary().Replace(name.Trim(), "$1_$2").Replace('-', '_').Replace(' ', '_').ToLowerInvariant();

    private static string EnumKey(string value) =>
        value.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');

    internal static string Compact(string description) =>
        Whitespace().Replace(description, " ").Trim();

    /// <summary>
    /// Drops what every optional parameter repeats: the "null" member of its
    /// type and enum, and "default": null. Omitting an optional argument
    /// already means null to the binder, so this changes no behaviour — it
    /// only stops a schema loaded by every session paying for it ~80 times.
    /// Idempotent, since the SDK may hand back the same tool objects.
    /// </summary>
    internal static JsonElement Slim(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object) return schema;
        var root = JsonNode.Parse(schema.GetRawText())!.AsObject();
        if (root["properties"] is not JsonObject props) return schema;

        foreach (var (_, node) in props)
        {
            if (node is not JsonObject prop) continue;
            if (prop["type"] is JsonArray types)
            {
                var kept = types.Where(t => t?.GetValue<string>() != "null").Select(t => t!.GetValue<string>()).ToList();
                prop["type"] = kept.Count == 1 ? JsonValue.Create(kept[0]) : new JsonArray([.. kept.Select(k => JsonValue.Create(k))]);
            }
            if (prop["enum"] is JsonArray choices && choices.Any(c => c is null))
                prop["enum"] = new JsonArray([.. choices.Where(c => c is not null).Select(c => c!.DeepClone())]);
            if (prop.ContainsKey("default") && prop["default"] is null)
                prop.Remove("default");
        }
        return JsonSerializer.SerializeToElement(root);
    }

    private static CallToolResult Text(string text, bool isError) =>
        new() { IsError = isError, Content = [new TextContentBlock { Text = text }] };

    /// <summary>Appends the guard's repairs to the result, and flags a tool's own "Error:" text as an error result.</summary>
    private static CallToolResult Annotate(CallToolResult result, IReadOnlyList<string> notes)
    {
        var first = result.Content.OfType<TextContentBlock>().FirstOrDefault();
        if (first is null) return result;
        if (first.Text.StartsWith("Error:", StringComparison.Ordinal)) result.IsError = true;
        if (notes.Count > 0) first.Text += $"\n(guard: {string.Join("; ", notes)})";
        return result;
    }

    [GeneratedRegex("([a-z0-9])([A-Z])")]
    private static partial Regex CamelBoundary();

    [GeneratedRegex(@"\s*\r?\n\s*")]
    private static partial Regex Whitespace();
}
