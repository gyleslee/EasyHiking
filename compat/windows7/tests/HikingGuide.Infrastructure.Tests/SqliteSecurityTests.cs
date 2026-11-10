using Microsoft.Data.Sqlite;

namespace HikingGuide.Infrastructure.Tests;

public sealed class SqliteSecurityTests
{
    [Fact]
    public void LoadedEngineContainsCve20256965Fix()
    {
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT sqlite_version()";
        var actual = (string)command.ExecuteScalar()!;
        Assert.True(Version.Parse(actual) >= new Version(3, 50, 2),
            $"实际加载的 SQLite {actual} 缺少 CVE-2025-6965 修复，最低要求 3.50.2。");
    }

    [Fact]
    public void UpdatedEnginePreservesUnicodeFtsAndTransactionRollback()
    {
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "CREATE VIRTUAL TABLE favorites_search USING fts5(name, description);" +
            "INSERT INTO favorites_search VALUES('中文收藏点', '山峰 照片');";
        command.ExecuteNonQuery();
        using (var transaction = db.BeginTransaction())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO favorites_search VALUES('未提交', '回滚');";
            command.ExecuteNonQuery();
            transaction.Rollback();
        }
        command.Transaction = null;
        command.CommandText = "SELECT name FROM favorites_search WHERE favorites_search MATCH '山峰'";
        Assert.Equal("中文收藏点", command.ExecuteScalar());
        command.CommandText = "SELECT count(*) FROM favorites_search";
        Assert.Equal(1L, command.ExecuteScalar());
        command.CommandText = "PRAGMA integrity_check";
        Assert.Equal("ok", command.ExecuteScalar());
    }
}
