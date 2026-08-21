using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ClipboardFloat.Core;

/// <summary>剪贴板历史持久化（SQLite）。</summary>
public sealed class HistoryStore : IHistoryPersister, IDisposable
{
    private readonly SqliteConnection _connection;

    public HistoryStore(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS history (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                hash TEXT NOT NULL UNIQUE,
                kind INTEGER NOT NULL,
                text TEXT,
                html TEXT,
                rtf TEXT,
                image_png BLOB,
                files TEXT,
                preview TEXT NOT NULL,
                captured_at TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public void SaveAdded(ClipItem item) => Upsert(item);

    /// <summary>插入或按哈希替换（去重）。</summary>
    public void Upsert(ClipItem item)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO history (hash, kind, text, html, rtf, image_png, files, preview, captured_at)
            VALUES ($hash, $kind, $text, $html, $rtf, $image, $files, $preview, $at)
            ON CONFLICT(hash) DO UPDATE SET
                kind = $kind, text = $text, html = $html, rtf = $rtf,
                image_png = $image, files = $files, preview = $preview, captured_at = $at
            """;
        cmd.Parameters.AddWithValue("$hash", item.Hash);
        cmd.Parameters.AddWithValue("$kind", (int)item.Kind);
        cmd.Parameters.AddWithValue("$text", (object?)item.Text ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$html", (object?)item.Html ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rtf", (object?)item.Rtf ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$image", (object?)item.ImagePng ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$files", item.Files is { Count: > 0 }
            ? JsonSerializer.Serialize(item.Files)
            : DBNull.Value);
        cmd.Parameters.AddWithValue("$preview", item.Preview);
        cmd.Parameters.AddWithValue("$at", item.CapturedAt.ToString("O", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    /// <summary>全部条目，最新在前（id 倒序）。</summary>
    public IReadOnlyList<ClipItem> LoadAll()
    {
        var list = new List<ClipItem>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT kind, text, html, rtf, image_png, files, preview, captured_at, hash
            FROM history ORDER BY id DESC
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var kind = (ClipKind)reader.GetInt32(0);
            string? text = reader.IsDBNull(1) ? null : reader.GetString(1);
            string? html = reader.IsDBNull(2) ? null : reader.GetString(2);
            string? rtf = reader.IsDBNull(3) ? null : reader.GetString(3);
            byte[]? image = reader.IsDBNull(4) ? null : (byte[])reader[4];
            string[]? files = reader.IsDBNull(5) ? null : JsonSerializer.Deserialize<string[]>(reader.GetString(5));
            string preview = reader.GetString(6);
            var capturedAt = DateTime.Parse(
                reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            string hash = reader.GetString(8);

            list.Add(new ClipItem(kind, text, html, rtf, image, files, preview, capturedAt, hash));
        }
        return list;
    }

    public void Delete(string hash)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM history WHERE hash = $hash";
        cmd.Parameters.AddWithValue("$hash", hash);
        cmd.ExecuteNonQuery();
    }

    public void Clear()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM history";
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();
}
