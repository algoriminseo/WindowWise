using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using WindowWise.Models;

namespace WindowWise.Services;

public sealed class ClipboardHistoryRepository
{
    private const int MaximumRegularItemCount = 300;

    private readonly string _connectionString;

    public ClipboardHistoryRepository()
    {
        string localAppDataPath = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        string windowWiseFolderPath = Path.Combine(localAppDataPath, "WindowWise");

        Directory.CreateDirectory(windowWiseFolderPath);

        string databasePath = Path.Combine(windowWiseFolderPath, "windowwise.db");
        _connectionString = $"Data Source={databasePath}";

        InitializeDatabase();
    }

    public IReadOnlyList<ClipboardInfo> LoadRecentItems()
    {
        var items = new List<ClipboardInfo>();

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                Id,
                Content,
                ContentType,
                CopiedAt,
                IsFavorite,
                Category,
                SourceAppName,
                IsSensitive,
                SensitiveReason
            FROM ClipboardItems
            ORDER BY IsFavorite DESC, CopiedAt DESC
            LIMIT $maximumItemCount;
            """;

        command.Parameters.AddWithValue("$maximumItemCount", MaximumRegularItemCount);

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            items.Add(new ClipboardInfo
            {
                Id = Guid.Parse(reader.GetString(0)),
                Content = reader.GetString(1),
                ContentType = Enum.Parse<ClipboardType>(reader.GetString(2)),
                CopiedAt = DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                IsFavorite = reader.GetBoolean(4),
                Category = reader.IsDBNull(5) ? null : reader.GetString(5),
                SourceAppName = reader.IsDBNull(6) ? null : reader.GetString(6),
                IsSensitive = reader.GetBoolean(7),
                SensitiveReason = reader.IsDBNull(8) ? null : reader.GetString(8)
            });
        }

        return items;
    }

    public void Upsert(ClipboardInfo item)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO ClipboardItems
            (
                Id,
                Content,
                ContentType,
                CopiedAt,
                IsFavorite,
                Category,
                SourceAppName,
                IsSensitive,
                SensitiveReason
            )
            VALUES
            (
                $id,
                $content,
                $contentType,
                $copiedAt,
                $isFavorite,
                $category,
                $sourceAppName,
                $isSensitive,
                $sensitiveReason
            )
            ON CONFLICT(Content) DO UPDATE SET
                ContentType = excluded.ContentType,
                CopiedAt = excluded.CopiedAt,
                IsFavorite = ClipboardItems.IsFavorite,
                Category = excluded.Category,
                SourceAppName = excluded.SourceAppName,
                IsSensitive = excluded.IsSensitive,
                SensitiveReason = excluded.SensitiveReason;
            """;

        command.Parameters.AddWithValue("$id", item.Id.ToString());
        command.Parameters.AddWithValue("$content", item.Content);
        command.Parameters.AddWithValue("$contentType", item.ContentType.ToString());
        command.Parameters.AddWithValue("$copiedAt", item.CopiedAt.ToString("O"));
        command.Parameters.AddWithValue("$isFavorite", item.IsFavorite);
        command.Parameters.AddWithValue("$category", (object?)item.Category ?? DBNull.Value);
        command.Parameters.AddWithValue("$sourceAppName", (object?)item.SourceAppName ?? DBNull.Value);
        command.Parameters.AddWithValue("$isSensitive", item.IsSensitive);
        command.Parameters.AddWithValue("$sensitiveReason", (object?)item.SensitiveReason ?? DBNull.Value);

        command.ExecuteNonQuery();

        DeleteOldRegularItems();
    }

    public void Delete(Guid id)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM ClipboardItems
            WHERE Id = $id;
            """;

        command.Parameters.AddWithValue("$id", id.ToString());
        command.ExecuteNonQuery();
    }

    public void ClearRegularItems()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM ClipboardItems
            WHERE IsFavorite = 0;
            """;

        command.ExecuteNonQuery();
    }

    private void InitializeDatabase()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS ClipboardItems
            (
                Id TEXT PRIMARY KEY,
                Content TEXT NOT NULL UNIQUE,
                ContentType TEXT NOT NULL,
                CopiedAt TEXT NOT NULL,
                IsFavorite INTEGER NOT NULL DEFAULT 0,
                Category TEXT NULL,
                SourceAppName TEXT NULL,
                IsSensitive INTEGER NOT NULL DEFAULT 0,
                SensitiveReason TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_ClipboardItems_CopiedAt
            ON ClipboardItems(CopiedAt DESC);

            CREATE INDEX IF NOT EXISTS IX_ClipboardItems_Content
            ON ClipboardItems(Content);
            """;

        command.ExecuteNonQuery();
    }

    private void DeleteOldRegularItems()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM ClipboardItems
            WHERE IsFavorite = 0
              AND Id NOT IN
              (
                  SELECT Id
                  FROM ClipboardItems
                  WHERE IsFavorite = 0
                  ORDER BY CopiedAt DESC
                  LIMIT $maximumRegularItemCount
              );
            """;

        command.Parameters.AddWithValue("$maximumRegularItemCount", MaximumRegularItemCount);
        command.ExecuteNonQuery();
    }
}
