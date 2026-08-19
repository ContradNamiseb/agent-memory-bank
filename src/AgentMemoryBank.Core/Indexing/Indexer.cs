using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using AgentMemoryBank.Models;
using AgentMemoryBank.Database;
using AgentMemoryBank.Indexing.Chunkers;

namespace AgentMemoryBank.Indexing;

/// <summary>What one repository's pass did. Errors are per-file and non-fatal.</summary>
public sealed record RepoIndexResult
{
    public required string Repo { get; init; }
    public int Indexed { get; init; }
    public int Skipped { get; init; }
    public int Removed { get; init; }
    public int Chunks { get; init; }
    public string? GitRev { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<string> FileErrors { get; init; } = [];
}

public sealed record IndexRun(long RunId, IReadOnlyList<RepoIndexResult> Results, TimeSpan Elapsed);

/// <summary>
/// What to skip while indexing. Passed in rather than read from <see cref="Config"/>
/// inside the loop, so a run is reproducible from its arguments alone — otherwise a
/// developer's local settings file silently changes what the tests index.
/// </summary>
public sealed record IndexOptions
{
    public IReadOnlySet<string> VendoredDirs { get; init; } = Config.VendoredDirs;
    public IReadOnlySet<string> BinaryExtensions { get; init; } = Config.BinaryExtensions;
    public long MaxFileBytes { get; init; } = Config.MaxFileBytes;

    /// <summary>Whatever the local configuration says. The CLI's default.</summary>
    public static IndexOptions FromConfig() => new();
}

/// <summary>
/// Phase A ingestion: deterministic, no LLM.
///
/// `git ls-files` per repo skipping vendored trees, hash each file, skip
/// unchanged, chunk by language, write file + chunk + FTS.
/// </summary>
public static class Indexer
{
    /// <summary>Index every requested repository, recording one run row for the pass.</summary>
    public static IndexRun Reindex(SqliteConnection db,
        string? repoRoot = null, IReadOnlyList<string>? repos = null,
        bool full = false, string? note = null, Action<string, int>? onFile = null,
        IndexOptions? options = null)
    {
        options ??= IndexOptions.FromConfig();
        var start = DateTime.UtcNow;
        repoRoot ??= Config.RepoRoot;
        repos ??= Config.Repos;

        var runId = MemoryDatabase.StartRun(db, tool: "phase-a-indexer", note: note);
        var results = new List<RepoIndexResult>();

        foreach (var repo in repos)
        {
            var repoDirectory = Path.Combine(repoRoot, repo);
            if (!GitFileScanner.IsRepository(repoDirectory))
            {
                results.Add(new RepoIndexResult { Repo = repo, Error = $"not a git repository: {repoDirectory}" });
                continue;
            }
            results.Add(IndexRepo(db, repo, repoDirectory, full, onFile, options));
        }

        // A run spans several repositories, so record the revisions together.
        using var stamp = db.Prepare("UPDATE run SET git_rev = $rev WHERE id = $id", "$rev", "$id");
        stamp.SetAll(
            string.Join(' ', results.Select(r => $"{r.Repo}@{Short(r.GitRev)}")),
            runId).ExecuteNonQuery();

        return new IndexRun(runId, results, DateTime.UtcNow - start);
    }

    private static string Short(string? rev) =>
        rev is null ? "none" : rev[..Math.Min(12, rev.Length)];

    /// <summary>
    /// Index one repository. Incremental by content hash: a file whose bytes are
    /// unchanged is not re-chunked, and a file that left the index takes its
    /// chunks (and their FTS rows) with it.
    /// </summary>
    public static RepoIndexResult IndexRepo(SqliteConnection db, string repo, string repoDirectory,
        bool full = false, Action<string, int>? onFile = null, IndexOptions? options = null)
    {
        options ??= IndexOptions.FromConfig();
        var revision = GitFileScanner.HeadRevision(repoDirectory);
        var now = DateTime.UtcNow.ToString("o");
        int indexed = 0, skipped = 0, removed = 0, chunkCount = 0;
        var fileErrors = new List<string>();

        var paths = GitFileScanner.ListFiles(repoDirectory)
            .Where(p => !IsVendored(p, options.VendoredDirs) && !IsBinaryPath(p, options.BinaryExtensions))
            .ToList();
        var present = new HashSet<string>(paths, StringComparer.Ordinal);

        using var transaction = db.BeginTransaction();

        using var selectFile = db.Prepare(
            "SELECT id, content_sha FROM file WHERE repo = $repo AND path = $path", "$repo", "$path");
        using var insertFile = db.Prepare("""
            INSERT INTO file(repo, path, lang, content_sha, git_rev, indexed_at)
            VALUES ($repo, $path, $lang, $sha, $rev, $at)
            RETURNING id
            """, "$repo", "$path", "$lang", "$sha", "$rev", "$at");
        using var updateFile = db.Prepare("""
            UPDATE file SET lang = $lang, content_sha = $sha, git_rev = $rev, indexed_at = $at
            WHERE id = $id
            """, "$lang", "$sha", "$rev", "$at", "$id");
        using var touchFile = db.Prepare(
            "UPDATE file SET git_rev = $rev, indexed_at = $at WHERE id = $id", "$rev", "$at", "$id");
        using var deleteChunks = db.Prepare("DELETE FROM chunk WHERE file_id = $id", "$id");
        using var deleteFile = db.Prepare("DELETE FROM file WHERE id = $id", "$id");
        using var insertChunk = db.Prepare("""
            INSERT INTO chunk(file_id, kind, symbol, line_start, line_end, body)
            VALUES ($file, $kind, $symbol, $start, $end, $body)
            """, "$file", "$kind", "$symbol", "$start", "$end", "$body");

        foreach (var command in new[] { selectFile, insertFile, updateFile, touchFile, deleteChunks, deleteFile, insertChunk })
            command.Transaction = transaction;

        foreach (var relativePath in paths)
        {
            var absolutePath = Path.Combine(repoDirectory, relativePath);
            byte[] bytes;
            try
            {
                var info = new FileInfo(absolutePath);
                // git ls-files reports submodule gitlinks as paths; they are directories on disk.
                if (!info.Exists || info.Length > options.MaxFileBytes) { skipped++; continue; }
                bytes = File.ReadAllBytes(absolutePath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                fileErrors.Add($"{relativePath}: {e.Message}");
                continue;
            }

            if (bytes.Contains((byte)0)) { skipped++; continue; }   // binary without a telling extension

            var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var existing = selectFile.SetAll(repo, relativePath)
                .Query(r => (Id: r.GetInt64(0), Sha: r.GetString(1)))
                .FirstOrDefault();

            if (existing.Id != 0 && existing.Sha == sha && !full)
            {
                touchFile.SetAll(revision, now, existing.Id).ExecuteNonQuery();
                skipped++;
                continue;
            }

            var language = Chunker.LanguageFor(relativePath);
            var chunks = Chunker.ChunkText(language, DecodeUtf8(bytes));

            long fileId;
            if (existing.Id != 0)
            {
                fileId = existing.Id;
                updateFile.SetAll(language, sha, revision, now, fileId).ExecuteNonQuery();
                deleteChunks.SetAll(fileId).ExecuteNonQuery();
            }
            else
            {
                fileId = (long)insertFile.SetAll(repo, relativePath, language, sha, revision, now).ExecuteScalar()!;
            }

            foreach (var chunk in chunks)
                insertChunk.SetAll(fileId, chunk.Kind, chunk.Symbol, chunk.LineStart, chunk.LineEnd, chunk.Body)
                    .ExecuteNonQuery();

            indexed++;
            chunkCount += chunks.Count;
            onFile?.Invoke(relativePath, chunks.Count);
        }

        // Files that left the repository must leave the index too.
        using var selectAll = db.Prepare("SELECT id, path FROM file WHERE repo = $repo", "$repo");
        selectAll.Transaction = transaction;
        var stored = selectAll.SetAll(repo).Query(r => (Id: r.GetInt64(0), Path: r.GetString(1)));
        foreach (var row in stored.Where(row => !present.Contains(row.Path)))
        {
            deleteChunks.SetAll(row.Id).ExecuteNonQuery();
            deleteFile.SetAll(row.Id).ExecuteNonQuery();
            removed++;
        }

        transaction.Commit();

        return new RepoIndexResult
        {
            Repo = repo,
            Indexed = indexed,
            Skipped = skipped,
            Removed = removed,
            Chunks = chunkCount,
            GitRev = revision,
            FileErrors = fileErrors,
        };
    }

    /// <summary>Decode as UTF-8, tolerating the odd stray byte rather than failing the file.</summary>
    private static string DecodeUtf8(byte[] bytes) =>
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false).GetString(bytes);

    public static bool IsVendored(string path, IReadOnlySet<string>? vendoredDirs = null) =>
        (vendoredDirs ?? Config.VendoredDirs).Contains(path.Split('/')[0]);

    public static bool IsBinaryPath(string path, IReadOnlySet<string>? binaryExtensions = null)
    {
        var name = Path.GetFileName(path);
        var dot = name.LastIndexOf('.');
        return dot > 0 && (binaryExtensions ?? Config.BinaryExtensions).Contains(name[(dot + 1)..]);
    }

    /// <summary>
    /// Merge the FTS index into fewer b-tree segments. Worth doing after a full
    /// reindex and pointless after an incremental one, so it is not automatic.
    /// </summary>
    public static void OptimizeFts(SqliteConnection db)
    {
        db.Execute("INSERT INTO chunk_fts(chunk_fts) VALUES ('optimize')");
        db.Execute("INSERT INTO note_fts(note_fts) VALUES ('optimize')");
    }
}
