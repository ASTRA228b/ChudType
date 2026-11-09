using System.Text.Json;
using ChudType.Core;
using Microsoft.Data.Sqlite;

namespace ChudType.Storage;

public sealed class LocalStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string connectionString;
    static LocalStore() => SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());

    public LocalStore()
        : this(File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.flag"))
            ? Path.Combine(AppContext.BaseDirectory, "data")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CHUDAI", "ChudType")) { }

    public LocalStore(string directory)
    {
        DirectoryPath = directory;
        Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "chudtype.db"), Pooling = false }.ToString();
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var schema = db.CreateCommand();
        schema.Transaction = tx;
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS history (id INTEGER PRIMARY KEY, time INTEGER NOT NULL, raw TEXT NOT NULL,
                text TEXT NOT NULL, language TEXT NOT NULL, seconds REAL NOT NULL, model TEXT NOT NULL, provider TEXT NOT NULL, words INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS usage (day TEXT NOT NULL, language TEXT NOT NULL, remote INTEGER NOT NULL,
                sessions INTEGER NOT NULL, words INTEGER NOT NULL, seconds REAL NOT NULL, PRIMARY KEY (day, language, remote));
            PRAGMA user_version = 1;
            """;
        schema.ExecuteNonQuery();
        tx.Commit();
    }

    public string DirectoryPath { get; }
    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();
        return db;
    }

    private T Read<T>(string key, T fallback)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT json FROM settings WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<T>(json) ?? fallback : fallback;
    }

    private void Write<T>(string key, T value)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO settings VALUES ($key, $json) ON CONFLICT(key) DO UPDATE SET json = excluded.json";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(value));
        cmd.ExecuteNonQuery();
    }

    public Preferences Preferences { get => Read("preferences", new Preferences()); set => Write("preferences", value); }
    public List<Vocabulary> Vocabulary { get => Read<List<Vocabulary>>("vocabulary", []); set => Write("vocabulary", value); }
    public List<Snippet> Snippets { get => Read<List<Snippet>>("snippets", []); set => Write("snippets", value); }

    public void Record(Transcript item, Preferences preferences)
    {
        if (!preferences.History && !preferences.Statistics) return;
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.Parameters.AddWithValue("$time", item.Time.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$language", item.Language);
        cmd.Parameters.AddWithValue("$seconds", item.Seconds);
        cmd.Parameters.AddWithValue("$words", item.Words);
        if (preferences.History)
        {
            cmd.CommandText = """
                INSERT INTO history (time, raw, text, language, seconds, model, provider, words)
                VALUES ($time, $raw, $text, $language, $seconds, $model, $provider, $words)
                """;
            cmd.Parameters.AddWithValue("$raw", item.Raw);
            cmd.Parameters.AddWithValue("$text", item.Text);
            cmd.Parameters.AddWithValue("$model", item.Model);
            cmd.Parameters.AddWithValue("$provider", item.Provider);
            cmd.ExecuteNonQuery();
        }
        if (preferences.Statistics)
        {
            cmd.CommandText = """
                INSERT INTO usage VALUES ($day, $language, $remote, 1, $words, $seconds)
                ON CONFLICT(day, language, remote) DO UPDATE SET
                sessions = sessions + 1, words = words + excluded.words, seconds = seconds + excluded.seconds
                """;
            cmd.Parameters.AddWithValue("$day", Day(item.Time.LocalDateTime));
            cmd.Parameters.AddWithValue("$remote", item.Provider is "ChudGPT" or "Custom" ? 1 : 0);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public IReadOnlyList<Transcript> History(string query = "")
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT id, time, raw, text, language, seconds, model, provider, words FROM history
            WHERE instr(lower(raw), lower($query)) > 0 OR instr(lower(text), lower($query)) > 0
            ORDER BY id DESC LIMIT 500
            """;
        cmd.Parameters.AddWithValue("$query", query);
        using var reader = cmd.ExecuteReader();
        var rows = new List<Transcript>();
        while (reader.Read())
            rows.Add(new(reader.GetInt64(0), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)),
                reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetDouble(5),
                reader.GetString(6), reader.GetString(7), reader.GetInt32(8)));
        return rows;
    }

    public void Edit(long id, string text) => Execute("UPDATE history SET text = $text, words = $words WHERE id = $id",
        ("$text", text), ("$words", TextCleanup.WordCount(text)), ("$id", id));
    public void Delete(long id) => Execute("DELETE FROM history WHERE id = $id", ("$id", id));
    public void ClearHistory() => Execute("DELETE FROM history");
    public void ClearUsage() => Execute("DELETE FROM usage");

    public void Prune(Preferences preferences)
    {
        if (preferences.HistoryDays > 0)
            Execute("DELETE FROM history WHERE time < $cutoff", ("$cutoff", DateTimeOffset.Now.AddDays(-preferences.HistoryDays).ToUnixTimeSeconds()));
    }

    public Usage GetUsage()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT coalesce(sum(words), 0), coalesce(sum(CASE WHEN day = $today THEN words ELSE 0 END), 0),
                coalesce(sum(CASE WHEN day >= $week THEN words ELSE 0 END), 0), coalesce(sum(seconds), 0),
                coalesce(sum(sessions), 0), coalesce(sum(sessions * remote), 0)
            FROM usage
            """;
        cmd.Parameters.AddWithValue("$today", Day(DateTime.Today));
        cmd.Parameters.AddWithValue("$week", Day(DateTime.Today.AddDays(-6)));
        using var reader = cmd.ExecuteReader();
        reader.Read();
        var result = new Usage(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetDouble(3),
            reader.GetInt32(4), reader.GetInt32(5), "");
        reader.Close();
        cmd.CommandText = "SELECT language, sum(sessions) FROM usage GROUP BY language ORDER BY sum(sessions) DESC LIMIT 5";
        using var languages = cmd.ExecuteReader();
        var display = new List<string>();
        while (languages.Read()) display.Add($"{languages.GetString(0)} ({languages.GetInt64(1)})");
        return result with { Languages = string.Join(" · ", display) };
    }

    public static string Export<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
    public static T Import<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonOptions)
        ?? throw new InvalidDataException("The file contains no data.");

    private void Execute(string sql, params (string Key, object Value)[] values)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (key, value) in values) cmd.Parameters.AddWithValue(key, value);
        cmd.ExecuteNonQuery();
    }

    private static string Day(DateTime value) => value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}
