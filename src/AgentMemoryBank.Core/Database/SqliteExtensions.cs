using Microsoft.Data.Sqlite;

namespace AgentMemoryBank.Database;

/// <summary>
/// Small conveniences over Microsoft.Data.Sqlite. Nothing clever: they exist so
/// the indexing and search code reads as SQL plus intent rather than as
/// command-and-parameter ceremony.
/// </summary>
public static class SqliteExtensions
{
    /// <summary>Run a statement (or several) that returns nothing.</summary>
    public static int Execute(this SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Build a command whose parameters are declared up front, so it can be
    /// reused across a loop: SQLite prepares it once and rebinds per execution.
    /// </summary>
    public static SqliteCommand Prepare(this SqliteConnection db, string sql, params string[] parameters)
    {
        var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var name in parameters) cmd.Parameters.Add(name, SqliteType.Text);
        return cmd;
    }

    /// <summary>Bind one parameter, mapping null to DBNull.</summary>
    public static SqliteCommand Set(this SqliteCommand cmd, string name, object? value)
    {
        cmd.Parameters[name].Value = value ?? DBNull.Value;
        return cmd;
    }

    /// <summary>Bind every parameter positionally, in the order they were declared.</summary>
    public static SqliteCommand SetAll(this SqliteCommand cmd, params object?[] values)
    {
        if (values.Length != cmd.Parameters.Count)
            throw new ArgumentException(
                $"expected {cmd.Parameters.Count} values for '{cmd.CommandText}', got {values.Length}");
        for (var i = 0; i < values.Length; i++) cmd.Parameters[i].Value = values[i] ?? DBNull.Value;
        return cmd;
    }

    /// <summary>Read rows into objects with a projection over the reader.</summary>
    public static List<T> Query<T>(this SqliteCommand cmd, Func<SqliteDataReader, T> read)
    {
        var rows = new List<T>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) rows.Add(read(reader));
        return rows;
    }

    /// <summary>Single scalar, or default when the query returned no rows or NULL.</summary>
    public static T? Scalar<T>(this SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? default : (T)Convert.ChangeType(value, typeof(T));
    }

    /// <summary>Nullable string column by name, since most of this schema allows NULL.</summary>
    public static string? GetStringOrNull(this SqliteDataReader reader, string column)
    {
        var i = reader.GetOrdinal(column);
        return reader.IsDBNull(i) ? null : reader.GetString(i);
    }

    public static long GetInt64(this SqliteDataReader reader, string column) =>
        reader.GetInt64(reader.GetOrdinal(column));

    public static int GetInt32(this SqliteDataReader reader, string column) =>
        reader.GetInt32(reader.GetOrdinal(column));

    public static double GetDouble(this SqliteDataReader reader, string column) =>
        reader.GetDouble(reader.GetOrdinal(column));

    public static string GetString(this SqliteDataReader reader, string column) =>
        reader.GetString(reader.GetOrdinal(column));
}
