using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace UEModManager.Migrations
{
    /// <inheritdoc />
    public partial class AddUserAdminAndLockFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Configurations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Key = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Configurations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ModCaches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ModId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ModName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Version = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    Author = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    GameName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    LocalPath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    DownloadUrl = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    FilePath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    IsInstalled = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsFavorite = table.Column<bool>(type: "INTEGER", nullable: false),
                    FileSize = table.Column<long>(type: "INTEGER", nullable: false),
                    InstallDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CacheTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CachedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DownloadCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Rating = table.Column<decimal>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModCaches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Email = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    Username = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    PasswordHash = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastLoginAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsLocked = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsAdmin = table.Column<bool>(type: "INTEGER", nullable: false),
                    Avatar = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FailedLoginAttempts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Email = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: true),
                    AttemptTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IpAddress = table.Column<string>(type: "TEXT", maxLength: 45, nullable: true),
                    UserAgent = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    FailureReason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FailedLoginAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FailedLoginAttempts_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "UserPreferences",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    DefaultGamePath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Language = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    Theme = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    AutoCheckUpdates = table.Column<bool>(type: "INTEGER", nullable: false),
                    AutoBackup = table.Column<bool>(type: "INTEGER", nullable: false),
                    ShowNotifications = table.Column<bool>(type: "INTEGER", nullable: false),
                    MinimizeToTray = table.Column<bool>(type: "INTEGER", nullable: false),
                    EnableCloudSync = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastSyncAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserPreferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserPreferences_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserSessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    SessionToken = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastAccessAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    DeviceInfo = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserSessions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "Configurations",
                columns: new[] { "Id", "CreatedAt", "Description", "Key", "UpdatedAt", "Value" },
                values: new object[,]
                {
                    { 1, new DateTime(2025, 8, 26, 2, 33, 10, 756, DateTimeKind.Local).AddTicks(6848), "应用程序版本", "AppVersion", new DateTime(2025, 8, 26, 2, 33, 10, 756, DateTimeKind.Local).AddTicks(6858), "1.7.36" },
                    { 2, new DateTime(2025, 8, 26, 2, 33, 10, 756, DateTimeKind.Local).AddTicks(6861), "数据库结构版本", "DatabaseVersion", new DateTime(2025, 8, 26, 2, 33, 10, 756, DateTimeKind.Local).AddTicks(6861), "1.0.0" },
                    { 3, new DateTime(2025, 8, 26, 2, 33, 10, 756, DateTimeKind.Local).AddTicks(6863), "是否首次运行", "FirstRun", new DateTime(2025, 8, 26, 2, 33, 10, 756, DateTimeKind.Local).AddTicks(6863), "true" },
                    { 4, new DateTime(2025, 8, 26, 2, 33, 10, 756, DateTimeKind.Local).AddTicks(6864), "云同步是否启用", "CloudSyncEnabled", new DateTime(2025, 8, 26, 2, 33, 10, 756, DateTimeKind.Local).AddTicks(6864), "false" },
                    { 5, new DateTime(2025, 8, 26, 2, 33, 10, 756, DateTimeKind.Local).AddTicks(6865), "最后云同步时间", "LastCloudSyncTime", new DateTime(2025, 8, 26, 2, 33, 10, 756, DateTimeKind.Local).AddTicks(6866), "1970-01-01T00:00:00Z" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Configurations_Key",
                table: "Configurations",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FailedLoginAttempts_AttemptTime",
                table: "FailedLoginAttempts",
                column: "AttemptTime");

            migrationBuilder.CreateIndex(
                name: "IX_FailedLoginAttempts_Email",
                table: "FailedLoginAttempts",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "IX_FailedLoginAttempts_UserId",
                table: "FailedLoginAttempts",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ModCaches_GameName_ModName",
                table: "ModCaches",
                columns: new[] { "GameName", "ModName" });

            migrationBuilder.CreateIndex(
                name: "IX_ModCaches_ModId",
                table: "ModCaches",
                column: "ModId");

            migrationBuilder.CreateIndex(
                name: "IX_UserPreferences_UserId",
                table: "UserPreferences",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_Email",
                table: "Users",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_SessionToken",
                table: "UserSessions",
                column: "SessionToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_UserId",
                table: "UserSessions",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Configurations");

            migrationBuilder.DropTable(
                name: "FailedLoginAttempts");

            migrationBuilder.DropTable(
                name: "ModCaches");

            migrationBuilder.DropTable(
                name: "UserPreferences");

            migrationBuilder.DropTable(
                name: "UserSessions");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}

