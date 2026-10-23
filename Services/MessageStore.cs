using System.Globalization;
using Microsoft.Data.Sqlite;
using SseApi.Models;

namespace SseApi.Services;

public class MessageStore(IConfiguration config)
{
    private readonly string _connectionString =
        config.GetConnectionString("Sqlite") ?? "Data Source=app.db";

    public void Init()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS messages (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                text       TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public Message Add(string text)
    {
        var createdAt = DateTime.UtcNow;
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO messages (text, created_at) VALUES ($text, $createdAt)
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("$text", text);
        cmd.Parameters.AddWithValue("$createdAt", createdAt.ToString("O"));
        var id = (long)cmd.ExecuteScalar()!;
        return new Message(id, text, createdAt);
    }

    public List<Message> GetAfter(long afterId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, text, created_at FROM messages WHERE id > $after ORDER BY id";
        cmd.Parameters.AddWithValue("$after", afterId);

        var result = new List<Message>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new Message(
                reader.GetInt64(0),
                reader.GetString(1),
                DateTime.Parse(reader.GetString(2), null, DateTimeStyles.RoundtripKind)));
        }
        return result;
    }

    /// <summary>
    /// Deletes every message. Ids are not reset (AUTOINCREMENT keeps counting), so a client that
    /// still remembers "last id I saw" never skips a new message.
    /// </summary>
    public void DeleteAll()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM messages";
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }
}
