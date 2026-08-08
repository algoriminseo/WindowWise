using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using WindowWise.Models;

namespace WindowWise.Services;


/// <summary>
/// Repository class for managing clipboard history items in a SQLite database.
/// This class provides methods to load, insert, update, and delete clipboard items,
/// as well as to manage the maximum number of regular items stored in the database.
/// </summary>
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
                SubCategory,
                SuggestedCategory,
                IsAiCategorized,
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
                SubCategory = reader.IsDBNull(6) ? null : reader.GetString(6),
                SuggestedCategory = reader.IsDBNull(7) ? null : reader.GetString(7),
                IsAiCategorized = reader.GetBoolean(8),
                SourceAppName = reader.IsDBNull(9) ? null : reader.GetString(9),
                IsSensitive = reader.GetBoolean(10),
                SensitiveReason = reader.IsDBNull(11) ? null : reader.GetString(11)
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
               SubCategory,
               SuggestedCategory,
               IsAiCategorized,
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
                $subCategory,
                $suggestedCategory,
                $isAiCategorized,
                $sourceAppName,
                $isSensitive,
                $sensitiveReason
            )
            ON CONFLICT(Content) DO UPDATE SET
                ContentType = excluded.ContentType,
                CopiedAt = excluded.CopiedAt,
                IsFavorite = ClipboardItems.IsFavorite,
                Category = excluded.Category,
                SubCategory = excluded.SubCategory,
                SuggestedCategory = excluded.SuggestedCategory,
                IsAiCategorized = excluded.IsAiCategorized,
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
        command.Parameters.AddWithValue("$subCategory", (object?)item.SubCategory ?? DBNull.Value);
        command.Parameters.AddWithValue("$suggestedCategory", (object?)item.SuggestedCategory ?? DBNull.Value);
        command.Parameters.AddWithValue("$isAiCategorized", item.IsAiCategorized);
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

    public void UpdateFavorite(Guid id, bool isFavorite)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE ClipboardItems
            SET IsFavorite = $isFavorite
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$isFavorite", isFavorite);
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
                   SubCategory TEXT NULL,
                   SuggestedCategory TEXT NULL,
                   IsAiCategorized INTEGER NOT NULL DEFAULT 0,
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

        EnsureColumnExists(connection, "ClipboardItems", "SubCategory", "TEXT NULL");
        EnsureColumnExists(connection, "ClipboardItems", "SuggestedCategory", "TEXT NULL");
        EnsureColumnExists(connection, "ClipboardItems", "IsAiCategorized", "INTEGER NOT NULL DEFAULT 0"); 
    }


    private static void EnsureColumnExists(SqliteConnection connection,
        string tableName, string columnName, string columnDefinition)
    {
        using var checkCommand = connection.CreateCommand();
        checkCommand.CommandText = $"PRAGMA table_info({tableName});";

        using var reader = checkCommand.ExecuteReader();

        while (reader.Read())
        {
            string existingColumnName = reader.GetString(1);

            if (string.Equals(existingColumnName, columnName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        using var alterCommand = connection.CreateCommand();
        alterCommand.CommandText =
            $"""
        ALTER TABLE {tableName}
        ADD COLUMN {columnName} {columnDefinition};
        """;

        alterCommand.ExecuteNonQuery();
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
