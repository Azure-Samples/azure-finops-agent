using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace AzureFinOps.Dashboard.Infrastructure;

/// <summary>
/// One in-memory SQLite database per conversation. QueryAzure stores every redacted response in table
/// responses; the model learns a result's shape and filters, joins, groups and calculates with its own
/// read-only SQL instead of receiving whole payloads. Bound to the exact owner and conversation; idle
/// databases expire after 30 minutes and on restart.
/// </summary>
internal sealed class ResultDatabase : IDisposable
{
    internal const int InlineCharacters = 32 * 1024;
    private const long ConversationCharacters = 64L * 1024 * 1024;
    private const long TotalCharacters = 512L * 1024 * 1024;
    private const int OutputCharacters = 48 * 1024;
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(15);
    private static readonly ConcurrentDictionary<(long Owner, string Session), ResultDatabase> Databases = new();

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _untrusted;
    private DateTime _deadline;
    private CancellationToken _cancellation;
    private long _characters;
    private DateTime _lastUsed = DateTime.UtcNow;

    private ResultDatabase()
    {
        _connection.Open();
        var handle = _connection.Handle!;
        raw.sqlite3_limit(handle, raw.SQLITE_LIMIT_ATTACHED, 0);
        raw.sqlite3_limit(handle, raw.SQLITE_LIMIT_LENGTH, 64 * 1024 * 1024);
        raw.sqlite3_limit(handle, raw.SQLITE_LIMIT_SQL_LENGTH, 64 * 1024);
        // Model SQL may only read: no writes, schema changes, pragmas, ATTACH or VACUUM INTO.
        raw.sqlite3_set_authorizer(handle, (delegate_authorizer)((_, action, _, _, _, _) => !_untrusted
            || action is raw.SQLITE_SELECT or raw.SQLITE_READ or raw.SQLITE_FUNCTION or raw.SQLITE_RECURSIVE ? raw.SQLITE_OK : raw.SQLITE_DENY), null);
        raw.sqlite3_progress_handler(handle, 10_000, _ => _untrusted && (DateTime.UtcNow > _deadline || _cancellation.IsCancellationRequested) ? 1 : 0, null);
        using var create = _connection.CreateCommand();
        create.CommandText = "CREATE TABLE responses(id INTEGER PRIMARY KEY, url TEXT NOT NULL, method TEXT NOT NULL, status INTEGER, retrieved_utc TEXT NOT NULL, body TEXT NOT NULL)";
        create.ExecuteNonQuery();
    }

    internal static ResultDatabase For(long owner, string session)
    {
        Sweep();
        return Databases.GetOrAdd((owner, session), _ => new ResultDatabase());
    }

    /// <summary>Stores a response and returns what the model sees: the response itself when small, otherwise its shape; with sql, the query rows.</summary>
    internal string Present(string url, string method, string response, string? sql, CancellationToken cancellationToken)
    {
        var (head, body) = Split(response);
        var id = Store(url, method, head, body);
        var stored = $"[Stored as responses.id = {id}.]";
        if (head.StartsWith("HTTP 4", StringComparison.Ordinal) || head.StartsWith("HTTP 5", StringComparison.Ordinal)) return response;
        if (!string.IsNullOrWhiteSpace(sql))
        {
            var rows = Query(sql, id, cancellationToken);
            return rows.StartsWith("Error", StringComparison.Ordinal)
                ? $"{head}{stored}\n{rows}\nThe request was not repeated. Correct the sql against this shape:\n{Shape(id)}"
                : $"{head}{stored}\n{rows}";
        }
        if (response.Length <= InlineCharacters) return $"{response.TrimEnd()}\n{stored}";
        return $"{head}{stored} The {body.Length.ToString("N0", CultureInfo.InvariantCulture)}-character body is too large to return; read only what you need with sql. Shape:\n{Shape(id)}";
    }

    /// <summary>Runs the model's read-only SQL; $id is the response stored by the same call.</summary>
    internal string Query(string sql, long? id, CancellationToken cancellationToken)
    {
        _gate.Wait(cancellationToken);
        try
        {
            _lastUsed = DateTime.UtcNow;
            using var command = _connection.CreateCommand();
            command.CommandText = sql;
            if (id is not null && sql.Contains("$id", StringComparison.Ordinal)) command.Parameters.AddWithValue("$id", id);
            (_untrusted, _deadline, _cancellation) = (true, DateTime.UtcNow + QueryTimeout, cancellationToken);
            using var reader = command.ExecuteReader();
            return Render(reader);
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException)
        {
            return "Error: SQL failed: " + exception.Message + (_untrusted && DateTime.UtcNow > _deadline ? " (the query exceeded 15 seconds; filter with json_each before joining or aggregating)" : "");
        }
        finally
        {
            _untrusted = false;
            _gate.Release();
        }
    }

    private long Store(string url, string method, string head, string body)
    {
        _gate.Wait();
        try
        {
            _lastUsed = DateTime.UtcNow;
            while (_characters + body.Length > ConversationCharacters && Scalar("SELECT min(id) FROM responses") is long oldest)
            {
                _characters -= (long)(Scalar("SELECT length(body) FROM responses WHERE id = $id", oldest) ?? 0L);
                Scalar("DELETE FROM responses WHERE id = $id", oldest);
            }
            using var insert = _connection.CreateCommand();
            insert.CommandText = "INSERT INTO responses(url, method, status, retrieved_utc, body) VALUES ($url, $method, $status, $time, $body) RETURNING id";
            insert.Parameters.AddWithValue("$url", url);
            insert.Parameters.AddWithValue("$method", method.ToUpperInvariant());
            insert.Parameters.AddWithValue("$status", head.StartsWith("HTTP ", StringComparison.Ordinal)
                && int.TryParse(head.AsSpan(5, Math.Min(3, head.Length - 5)), out var status) ? status : DBNull.Value);
            insert.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("$body", body);
            _characters += body.Length;
            return (long)insert.ExecuteScalar()!;
        }
        finally { _gate.Release(); }
    }

    private string Shape(long id)
    {
        _gate.Wait();
        try
        {
            if (Scalar("SELECT json_valid(body) FROM responses WHERE id = $id", id) is not 1L)
                return $"text, {Scalar("SELECT length(body) FROM responses WHERE id = $id", id)} characters. Search it with instr and substr, e.g. SELECT substr(body, instr(body, 'term') - 200, 2000) FROM responses WHERE id = {id}";
            using var command = _connection.CreateCommand();
            // The first element of every array stands for its siblings; every column name of a columnar table is listed.
            command.CommandText = """
                SELECT t.fullkey AS path, t.type, CASE t.type WHEN 'array' THEN json_array_length(t.value) END AS items,
                       CASE WHEN t.type NOT IN ('object', 'array') THEN substr(t.value, 1, 60) END AS example
                FROM responses r, json_tree(r.body) t
                WHERE r.id = $id AND (t.fullkey NOT GLOB '*[[][1-9]*' OR t.fullkey GLOB '*columns[[][0-9]*].name')
                  AND length(t.fullkey) - length(replace(replace(t.fullkey, '.', ''), '[', '')) <= 5
                LIMIT 150
                """;
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            return Render(reader);
        }
        finally { _gate.Release(); }
    }

    private object? Scalar(string sql, long? id = null)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        if (id is not null) command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is var value and not DBNull ? value : null;
    }

    private static string Render(SqliteDataReader reader)
    {
        var text = new StringBuilder().AppendJoin('\t', Enumerable.Range(0, reader.FieldCount).Select(reader.GetName)).Append('\n');
        int rows = 0, shown = 0;
        while (reader.Read())
        {
            rows++;
            if (text.Length > OutputCharacters) continue;
            for (var column = 0; column < reader.FieldCount; column++)
                text.Append(column == 0 ? "" : "\t").Append(reader.IsDBNull(column) ? "null" : Cell(reader.GetValue(column)));
            text.Append('\n');
            shown++;
        }
        return text.Append(shown == rows ? $"({rows} rows)" : $"(showing {shown} of {rows} rows; aggregate, filter or page with LIMIT and OFFSET)").ToString();
    }

    private static string Cell(object value) => (value switch
    {
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        byte[] bytes => $"<{bytes.Length} bytes>",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
    }).Replace('\t', ' ').Replace("\r", "", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

    // Leading status, timestamp and page metadata lines stay with the answer; the rest is the stored body.
    internal static (string Head, string Body) Split(string response)
    {
        var start = 0;
        while (start < response.Length)
        {
            var end = response.IndexOf('\n', start);
            if (end < 0) break;
            var line = response.AsSpan(start, end - start).TrimEnd('\r');
            if (!(line.IsEmpty || start == 0 && line.StartsWith("HTTP ") || line.StartsWith(ResponseShaper.TimestampPrefix)
                || line.StartsWith("Final URL: ") || line.StartsWith("Content-Type: ") || line.StartsWith("Bytes on wire: ") || line.StartsWith("UTC: "))) break;
            start = end + 1;
        }
        return (response[..start], response[start..]);
    }

    private static void Sweep()
    {
        var now = DateTime.UtcNow;
        var ordered = Databases.OrderBy(entry => entry.Value._lastUsed).ToList();
        var total = ordered.Sum(entry => entry.Value._characters);
        foreach (var (key, database) in ordered)
        {
            if (now - database._lastUsed < Idle && total <= TotalCharacters) break;
            if (!Databases.TryRemove(key, out _)) continue;
            total -= database._characters;
            database.Dispose();
        }
    }

    public void Dispose()
    {
        _gate.Wait();
        _connection.Dispose();
    }
}
