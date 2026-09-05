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
                CategoryIsManual,
                SourceAppName,
                SourceUrl,
                IsSensitive,
                SensitiveReason,
                SensitivityConfidence,
                ProtectionState
            FROM ClipboardItems
            ORDER BY IsFavorite DESC, CopiedAt DESC
            LIMIT $maximumItemCount;
            """;

        command.Parameters.AddWithValue("$maximumItemCount", MaximumRegularItemCount);

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            SensitivityConfidence sensitivityConfidence = reader.IsDBNull(11)
                ? SensitivityConfidence.None
                : Enum.Parse<SensitivityConfidence>(reader.GetString(11));

            ProtectionState protectionState = reader.IsDBNull(12)
                ? GetDefaultProtectionState(sensitivityConfidence)
                : Enum.Parse<ProtectionState>(reader.GetString(12));

            if (protectionState == ProtectionState.None &&
                sensitivityConfidence != SensitivityConfidence.None)
            {
                protectionState = GetDefaultProtectionState(sensitivityConfidence);
            }

            items.Add(new ClipboardInfo
            {
                Id = Guid.Parse(reader.GetString(0)),
                Content = reader.GetString(1),
                ContentType = Enum.Parse<ClipboardType>(reader.GetString(2)),
                CopiedAt = DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                IsFavorite = reader.GetBoolean(4),
                Category = reader.IsDBNull(5) ? null : reader.GetString(5),
                IsCategoryManuallyAssigned = reader.GetBoolean(6),
                SourceAppName = reader.IsDBNull(7) ? null : reader.GetString(7),
                SourceUrl = reader.IsDBNull(8) ? null : reader.GetString(8),
                IsSensitive = reader.GetBoolean(9),
                SensitiveReason = reader.IsDBNull(10) ? null : reader.GetString(10),
                SensitivityConfidence = sensitivityConfidence,
                ProtectionState = protectionState
            });
        }

        return items;
    }

    public IReadOnlyList<ClipboardCategoryRule> LoadCategoryRules()
    {
        var rules = new List<ClipboardCategoryRule>();

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                Id,
                Name,
                Keywords,
                ColorHex
            FROM ClipboardCategoryRules
            ORDER BY CreatedAt ASC;
            """;

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            string[] keywords = reader.GetString(2)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            rules.Add(new ClipboardCategoryRule
            {
                Id = Guid.Parse(reader.GetString(0)),
                Name = reader.GetString(1),
                Keywords = keywords,
                ColorHex = reader.IsDBNull(3) ? "#2563EB" : reader.GetString(3)
            });
        }

        return rules;
    }

    public void AddCategoryRule(ClipboardCategoryRule rule)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO ClipboardCategoryRules
            (
                Id,
                Name,
                Keywords,
                ColorHex,
                CreatedAt
            )
            VALUES
            (
                $id,
                $name,
                $keywords,
                $colorHex,
                $createdAt
            );
            """;

        command.Parameters.AddWithValue("$id", rule.Id.ToString());
        command.Parameters.AddWithValue("$name", rule.Name);
        command.Parameters.AddWithValue("$keywords", string.Join(",", rule.Keywords));
        command.Parameters.AddWithValue("$colorHex", rule.ColorHex);
        command.Parameters.AddWithValue("$createdAt", DateTimeOffset.Now.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void DeleteCategoryRule(Guid id)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM ClipboardCategoryRules
            WHERE Id = $id;
            """;

        command.Parameters.AddWithValue("$id", id.ToString());
        command.ExecuteNonQuery();
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
               CategoryIsManual,
               SourceAppName,
               SourceUrl,
               IsSensitive,
               SensitiveReason,
               SensitivityConfidence,
               ProtectionState
            )
            VALUES
            (
                $id,
                $content,
                $contentType,
                $copiedAt,
                $isFavorite,
                $category,
                $categoryIsManual,
                $sourceAppName,
                $sourceUrl,
                $isSensitive,
                $sensitiveReason,
                $sensitivityConfidence,
                $protectionState
            )
            ON CONFLICT(Content) DO UPDATE SET
                ContentType = excluded.ContentType,
                CopiedAt = excluded.CopiedAt,
                IsFavorite = ClipboardItems.IsFavorite,
                Category = excluded.Category,
                CategoryIsManual = excluded.CategoryIsManual,
                SourceAppName = excluded.SourceAppName,
                SourceUrl = excluded.SourceUrl,
                IsSensitive = excluded.IsSensitive,
                SensitiveReason = excluded.SensitiveReason,
                SensitivityConfidence = excluded.SensitivityConfidence,
                ProtectionState = excluded.ProtectionState;
            """;
        command.Parameters.AddWithValue("$id", item.Id.ToString());
        command.Parameters.AddWithValue("$content", item.Content);
        command.Parameters.AddWithValue("$contentType", item.ContentType.ToString());
        command.Parameters.AddWithValue("$copiedAt", item.CopiedAt.ToString("O"));
        command.Parameters.AddWithValue("$isFavorite", item.IsFavorite);
        command.Parameters.AddWithValue("$category", (object?)item.Category ?? DBNull.Value);
        command.Parameters.AddWithValue("$categoryIsManual", item.IsCategoryManuallyAssigned);
        command.Parameters.AddWithValue("$sourceAppName", (object?)item.SourceAppName ?? DBNull.Value);
        command.Parameters.AddWithValue("$sourceUrl", (object?)item.SourceUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("$isSensitive", item.IsSensitive);
        command.Parameters.AddWithValue("$sensitiveReason", (object?)item.SensitiveReason ?? DBNull.Value);
        command.Parameters.AddWithValue("$sensitivityConfidence", item.SensitivityConfidence.ToString());
        command.Parameters.AddWithValue("$protectionState", item.ProtectionState.ToString());

        command.ExecuteNonQuery();

        DeleteOldRegularItems();
    }

    public void UpdateProtectionState(ClipboardInfo item)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE ClipboardItems
            SET
                Content = $content,
                ContentType = $contentType,
                IsSensitive = $isSensitive,
                SensitiveReason = $sensitiveReason,
                SensitivityConfidence = $sensitivityConfidence,
                ProtectionState = $protectionState
            WHERE Id = $id;
            """;

        command.Parameters.AddWithValue("$id", item.Id.ToString());
        command.Parameters.AddWithValue("$content", item.Content);
        command.Parameters.AddWithValue("$contentType", item.ContentType.ToString());
        command.Parameters.AddWithValue("$isSensitive", item.IsSensitive);
        command.Parameters.AddWithValue("$sensitiveReason", (object?)item.SensitiveReason ?? DBNull.Value);
        command.Parameters.AddWithValue("$sensitivityConfidence", item.SensitivityConfidence.ToString());
        command.Parameters.AddWithValue("$protectionState", item.ProtectionState.ToString());
        command.ExecuteNonQuery();
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
                   CategoryIsManual INTEGER NOT NULL DEFAULT 0,
                   SourceAppName TEXT NULL,
                   SourceUrl TEXT NULL,
                   IsSensitive INTEGER NOT NULL DEFAULT 0,
                   SensitiveReason TEXT NULL,
                   SensitivityConfidence TEXT NOT NULL DEFAULT 'None',
                   ProtectionState TEXT NOT NULL DEFAULT 'None'
            );

            CREATE INDEX IF NOT EXISTS IX_ClipboardItems_CopiedAt
            ON ClipboardItems(CopiedAt DESC);

            CREATE INDEX IF NOT EXISTS IX_ClipboardItems_Content
            ON ClipboardItems(Content);

            CREATE TABLE IF NOT EXISTS ClipboardCategoryRules
            (
                   Id TEXT PRIMARY KEY,
                   Name TEXT NOT NULL UNIQUE,
                   Keywords TEXT NOT NULL,
                   ColorHex TEXT NOT NULL DEFAULT '#2563EB',
                   CreatedAt TEXT NOT NULL
            );
            """;

        command.ExecuteNonQuery();

        EnsureColumnExists(connection, "ClipboardCategoryRules", "ColorHex", "TEXT NOT NULL DEFAULT '#2563EB'");
        EnsureColumnExists(connection, "ClipboardItems", "CategoryIsManual", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumnExists(connection, "ClipboardItems", "SourceAppName", "TEXT NULL");
        EnsureColumnExists(connection, "ClipboardItems", "SourceUrl", "TEXT NULL");
        EnsureColumnExists(connection, "ClipboardItems", "IsSensitive", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumnExists(connection, "ClipboardItems", "SensitiveReason", "TEXT NULL"); 
        EnsureColumnExists(connection, "ClipboardItems", "SensitivityConfidence", "TEXT NOT NULL DEFAULT 'None'");
        EnsureColumnExists(connection, "ClipboardItems", "ProtectionState", "TEXT NOT NULL DEFAULT 'None'");
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

    private static ProtectionState GetDefaultProtectionState(SensitivityConfidence confidence)
    {
        return confidence switch
        {
            SensitivityConfidence.High => ProtectionState.Blocked,
            SensitivityConfidence.Possible => ProtectionState.NeedsReview,
            _ => ProtectionState.None
        };
    }
}
