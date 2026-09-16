using Microsoft.Data.Sqlite;
using Shouldly;

namespace PowerLedger.Service.Tests;

public class TestDatabaseTests
{
    [Fact]
    public void Disposing_one_leaves_alone_a_connection_that_a_test_running_alongside_is_opening()
    {
        using var other = new TestDatabase();
        using var connection = other.Db.Open();

        using (MidOpen.Hold(connection))
        {
            new TestDatabase().Dispose();
        }

        new SqliteCommand("SELECT 1", connection).ExecuteScalar().ShouldBe(1L);
    }
}
