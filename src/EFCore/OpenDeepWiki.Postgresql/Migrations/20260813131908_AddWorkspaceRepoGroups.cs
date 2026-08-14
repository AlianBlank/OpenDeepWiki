using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using System;

#nullable disable

namespace OpenDeepWiki.Postgresql.Migrations;

[DbContext(typeof(PostgresqlDbContext))]
[Migration("20260813131908_AddWorkspaceRepoGroups")]
public partial class AddWorkspaceRepoGroups : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // WorkspaceRepoGroup 表
        migrationBuilder.CreateTable(
            name: "WorkspaceRepoGroups",
            columns: table => new
            {
                Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                BasePath = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                LanguagesCsv = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, defaultValue: "en"),
                CatalogTemplatePath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                DomainPromptsPath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                WriterType = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                WriterOptionsJson = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                OutputRoot = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                LastRunAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                LastRunStatus = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                LastRunError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                DeletedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                IsDeleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                Version = table.Column<byte[]>(type: "bytea", nullable: true)
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
                Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                GroupId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                RepoKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                GitUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                LocalPath = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                Domain = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "tools"),
                Branch = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true, defaultValue: "main"),
                Active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                DisplayOrder = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                DeletedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                IsDeleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                Version = table.Column<byte[]>(type: "bytea", nullable: true)
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

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "RepoRefs");
        migrationBuilder.DropTable(name: "WorkspaceRepoGroups");
    }
}
