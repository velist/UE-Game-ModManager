using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using UEModManager.Data;
using UEModManager.Models;
using UEModManager.Services;

namespace UEModManager.Tests.Services;

public sealed class LocalAccountQueryTests
{
    [Fact]
    public async Task EmptyDatabase_ReturnsAnEmptyAccountList()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var database = CreateDatabase(connection);
        await database.Database.EnsureCreatedAsync();

        var service = new LocalAuthService(database, NullLogger<LocalAuthService>.Instance);

        Assert.Empty(await service.GetAllUsersAsync());
    }

    [Fact]
    public async Task Accounts_AreReturnedNewestFirst()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var database = CreateDatabase(connection);
        await database.Database.EnsureCreatedAsync();
        var oldAccount = new LocalUser
        {
            Email = "older@test.local", PasswordHash = "test", CreatedAt = new DateTime(2026, 1, 1)
        };
        var newAccount = new LocalUser
        {
            Email = "newer@test.local", PasswordHash = "test", CreatedAt = new DateTime(2026, 2, 1)
        };
        database.Users.AddRange(oldAccount, newAccount);
        await database.SaveChangesAsync();
        var service = new LocalAuthService(database, NullLogger<LocalAuthService>.Instance);

        var accounts = (await service.GetAllUsersAsync()).ToArray();

        Assert.Equal(new[] { newAccount.Id, oldAccount.Id }, accounts.Select(account => account.Id));
    }

    [Fact]
    public async Task MissingAccountTable_PropagatesFailureInsteadOfReportingAnEmptyDatabase()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var database = CreateDatabase(connection);
        // 真实连接可用，但 Users 表缺失；管理页不能把查询失败显示成“零用户、数据库正常”。
        var service = new LocalAuthService(database, NullLogger<LocalAuthService>.Instance);

        await Assert.ThrowsAsync<SqliteException>(() => service.GetAllUsersAsync());
    }

    private static LocalDbContext CreateDatabase(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);
}
