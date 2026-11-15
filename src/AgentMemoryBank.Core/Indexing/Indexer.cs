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
            var repoDirectory = Config.GetRepoDirectory(repo, repoRoot);
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
        using var cmds = new RepoIndexCommands(db, transaction);

        foreach (var relativePath in paths)
        {
            if (!TryReadSourceBytes(repoDirectory, relativePath, options.MaxFileBytes, fileErrors,
                    out var bytes, out var fileSkipped))
            {
                if (fileSkipped) skipped++;
                continue;
            }

            if (!UpsertIndexedFile(cmds, repo, relativePath, bytes, revision, now, full, out var chunksWritten))
            {
                skipped++;
                continue;
            }

            indexed++;
            chunkCount += chunksWritten;
            onFile?.Invoke(relativePath, chunksWritten);
        }

        // Files that left the repository must leave the index too.
        removed = PruneDeletedFiles(cmds, repo, present);

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

    private sealed class RepoIndexCommands : IDisposable
    {
        public SqliteCommand SelectFile { get; }
        public SqliteCommand InsertFile { get; }
        public SqliteCommand UpdateFile { get; }
        public SqliteCommand TouchFile { get; }
        public SqliteCommand DeleteChunks { get; }
        public SqliteCommand DeleteFile { get; }
        public SqliteCommand InsertChunk { get; }
        public SqliteCommand SelectAllFiles { get; }

        public RepoIndexCommands(SqliteConnection db, SqliteTransaction transaction)
        {
            SelectFile = db.Prepare(
                "SELECT id, content_sha FROM file WHERE repo = $repo AND path = $path", "$repo", "$path");
            InsertFile = db.Prepare("""
                INSERT INTO file(repo, path, lang, content_sha, git_rev, indexed_at)
                VALUES ($repo, $path, $lang, $sha, $rev, $at)
                RETURNING id
                """, "$repo", "$path", "$lang", "$sha", "$rev", "$at");
            UpdateFile = db.Prepare("""
                UPDATE file SET lang = $lang, content_sha = $sha, git_rev = $rev, indexed_at = $at
                WHERE id = $id
                """, "$lang", "$sha", "$rev", "$at", "$id");
            TouchFile = db.Prepare(
                "UPDATE file SET git_rev = $rev, indexed_at = $at WHERE id = $id", "$rev", "$at", "$id");
            DeleteChunks = db.Prepare("DELETE FROM chunk WHERE file_id = $id", "$id");
            DeleteFile = db.Prepare("DELETE FROM file WHERE id = $id", "$id");
            InsertChunk = db.Prepare("""
                INSERT INTO chunk(file_id, kind, symbol, line_start, line_end, body)
                VALUES ($file, $kind, $symbol, $start, $end, $body)
                """, "$file", "$kind", "$symbol", "$start", "$end", "$body");
            SelectAllFiles = db.Prepare("SELECT id, path FROM file WHERE repo = $repo", "$repo");

            foreach (var command in new[] { SelectFile, InsertFile, UpdateFile, TouchFile, DeleteChunks, DeleteFile, InsertChunk, SelectAllFiles })
                command.Transaction = transaction;
        }

        public void Dispose()
        {
            SelectFile.Dispose();
            InsertFile.Dispose();
            UpdateFile.Dispose();
            TouchFile.Dispose();
            DeleteChunks.Dispose();
            DeleteFile.Dispose();
            InsertChunk.Dispose();
            SelectAllFiles.Dispose();
        }
    }

    private static bool TryReadSourceBytes(string repoDirectory, string relativePath, long maxFileBytes,
        List<string> fileErrors, out byte[] bytes, out bool skipped)
    {
        bytes = [];
        skipped = false;
        var absolutePath = Path.Combine(repoDirectory, relativePath);
        try
        {
            var info = new FileInfo(absolutePath);
            // git ls-files reports submodule gitlinks as paths; they are directories on disk.
            if (!info.Exists || info.Length > maxFileBytes)
            {
                skipped = true;
                return false;
            }
            bytes = File.ReadAllBytes(absolutePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            fileErrors.Add($"{relativePath}: {e.Message}");
            return false;
        }

        if (bytes.Contains((byte)0))   // binary without a telling extension
        {
            skipped = true;
            return false;
        }

        return true;
    }

    private static bool UpsertIndexedFile(RepoIndexCommands cmds, string repo, string relativePath,
        byte[] bytes, string? revision, string now, bool full, out int chunksWritten)
    {
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var existing = cmds.SelectFile.SetAll(repo, relativePath)
            .Query(r => (Id: r.GetInt64(0), Sha: r.GetString(1)))
            .FirstOrDefault();

        if (existing.Id != 0 && existing.Sha == sha && !full)
        {
            cmds.TouchFile.SetAll(revision, now, existing.Id).ExecuteNonQuery();
            chunksWritten = 0;
            return false;
        }

        var language = Chunker.LanguageFor(relativePath);
        var chunks = Chunker.ChunkText(language, DecodeUtf8(bytes));

        long fileId;
        if (existing.Id != 0)
        {
            fileId = existing.Id;
            cmds.UpdateFile.SetAll(language, sha, revision, now, fileId).ExecuteNonQuery();
            cmds.DeleteChunks.SetAll(fileId).ExecuteNonQuery();
        }
        else
        {
            fileId = (long)cmds.InsertFile.SetAll(repo, relativePath, language, sha, revision, now).ExecuteScalar()!;
        }

        foreach (var chunk in chunks)
            cmds.InsertChunk.SetAll(fileId, chunk.Kind, chunk.Symbol, chunk.LineStart, chunk.LineEnd, chunk.Body)
                .ExecuteNonQuery();

        chunksWritten = chunks.Count;
        return true;
    }

    private static int PruneDeletedFiles(RepoIndexCommands cmds, string repo, HashSet<string> present)
    {
        var removed = 0;
        foreach (var row in cmds.SelectAllFiles.SetAll(repo).Query(r => (Id: r.GetInt64(0), Path: r.GetString(1))))
        {
            if (present.Contains(row.Path)) continue;
            cmds.DeleteChunks.SetAll(row.Id).ExecuteNonQuery();
            cmds.DeleteFile.SetAll(row.Id).ExecuteNonQuery();
            removed++;
        }
        return removed;
    }

    /// <summary>Decode as UTF-8, tolerating the odd stray byte rather than failing the file.</summary>
    private static string DecodeUtf8(byte[] bytes) =>
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false).GetString(bytes);

    public static bool IsVendored(string path, IReadOnlySet<string>? vendoredDirs = null)
    {
        var sep = path.IndexOfAny(['/', '\\']);
        var firstSegment = sep < 0 ? path : path[..sep];
        return (vendoredDirs ?? Config.VendoredDirs).Contains(firstSegment);
    }

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
