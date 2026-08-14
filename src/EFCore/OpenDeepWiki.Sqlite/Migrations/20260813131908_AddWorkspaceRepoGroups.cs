using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenDeepWiki.Sqlite.Migrations;

/// <summary>
/// 新增 WorkspaceRepoGroup + RepoRef 两张表，用于 GameFrameX 工作区仓组摄取与文档写入。
/// 注意：本 migration 为手写版，只创建新表，不触碰上游既有表（避免与上游 snapshot 不同步引起的误判）。
/// </summary>
public partial class AddWorkspaceRepoGroups : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // WorkspaceRepoGroup 表
        migrationBuilder.CreateTable(
            name: "WorkspaceRepoGroups",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                BasePath = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                LanguagesCsv = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false, defaultValue: "en"),
                CatalogTemplatePath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                DomainPromptsPath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                WriterType = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                WriterOptionsJson = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                OutputRoot = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                LastRunAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                LastRunStatus = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                LastRunError = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                DeletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                Version = table.Column<byte[]>(type: "BLOB", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_WorkspaceRepoGroups", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_WorkspaceRepoGroups_IsDeleted",
            table: "WorkspaceRepoGroups",
            column: "IsDeleted");

        migrationBuilder.CreateIndex(
            name: "IX_WorkspaceRepoGroups_LastRunStatus",
            table: "WorkspaceRepoGroups",
            column: "LastRunStatus");

        // RepoRef 表
        migrationBuilder.CreateTable(
            name: "RepoRefs",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                GroupId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                RepoKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                GitUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                LocalPath = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                Domain = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, defaultValue: "tools"),
                Branch = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true, defaultValue: "main"),
                Active = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                DisplayOrder = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                DeletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                Version = table.Column<byte[]>(type: "BLOB", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RepoRefs", x => x.Id);
                table.ForeignKey(
                    name: "FK_RepoRefs_WorkspaceRepoGroups_GroupId",
                    column: x => x.GroupId,
                    principalTable: "WorkspaceRepoGroups",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_RepoRefs_GroupId_RepoKey",
            table: "RepoRefs",
            columns: new[] { "GroupId", "RepoKey" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_RepoRefs_Active",
            table: "RepoRefs",
            column: "Active");

        migrationBuilder.CreateIndex(
            name: "IX_RepoRefs_DisplayOrder",
            table: "RepoRefs",
            column: "DisplayOrder");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "RepoRefs");
        migrationBuilder.DropTable(name: "WorkspaceRepoGroups");
    }
}
