using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.Workspaces;
using OpenDeepWiki.Sqlite;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Workspaces;

/// <summary>
/// WorkspaceService upsert / 查询 / 状态标记 契约测试（v4 目录树 registry）。
/// 用真实 SQLite 文件库（EnsureCreated），避免 InMemory 对 ForeignKey/Unique 约束的弱支持。
/// </summary>
public class WorkspaceServiceTests
{
    [Fact]
    public async Task UpsertFromRegistryAsync_TopLevelGroup_AggregatesActiveSubtreeRepos()
    {
        await using var fixture = await SqliteFixture.CreateAsync();
        var registryPath = TestRegistryJson.WriteToTempFile();

        var svc = new WorkspaceService(fixture.Context, NullLogger<WorkspaceService>.Instance);

        var group = await svc.UpsertFromRegistryAsync(registryPath, "gfx-test");

        Assert.Equal("gfx-test", group.Id);
        Assert.Equal("Test Group", group.Name);
        // 子树聚合：client 的 unity + shared 的 foundation；inactive-repo 条目停用、paused 子组整棵剪枝
        Assert.Equal(2, group.Repos.Count);
        Assert.All(group.Repos, r => Assert.True(r.Active));
        Assert.DoesNotContain(group.Repos, r => r.RepoKey == "paused-repo");
        Assert.DoesNotContain(group.Repos, r => r.RepoKey == "inactive-repo");
    }

    [Fact]
    public async Task UpsertFromRegistryAsync_SubGroupPath_AddressesSubtree()
    {
        await using var fixture = await SqliteFixture.CreateAsync();
        var registryPath = TestRegistryJson.WriteToTempFile();

        var svc = new WorkspaceService(fixture.Context, NullLogger<WorkspaceService>.Instance);

        var group = await svc.UpsertFromRegistryAsync(registryPath, "gfx-test/client");

        Assert.Equal("gfx-test/client", group.Id);
        Assert.Equal("客户端", group.Name);
        var repo = Assert.Single(group.Repos);
        Assert.Equal("unity", repo.RepoKey);
        Assert.Equal("https://github.com/GameFrameX/GameFrameX.Unity.git", repo.GitUrl);
    }

    [Fact]
    public async Task UpsertFromRegistryAsync_StandaloneGroup_FiltersInactiveEntries()
    {
        await using var fixture = await SqliteFixture.CreateAsync();
        var registryPath = TestRegistryJson.WriteToTempFile();

        var svc = new WorkspaceService(fixture.Context, NullLogger<WorkspaceService>.Instance);

        var group = await svc.UpsertFromRegistryAsync(registryPath, "gfx-pkgs");

        var repo = Assert.Single(group.Repos);
        Assert.Equal("com.gameframex.unity.config", repo.RepoKey);
    }

    [Fact]
    public async Task UpsertFromRegistryAsync_SecondCall_UpdatesExisting()
    {
        await using var fixture = await SqliteFixture.CreateAsync();
        var registryPath = TestRegistryJson.WriteToTempFile();

        var svc = new WorkspaceService(fixture.Context, NullLogger<WorkspaceService>.Instance);
        await svc.UpsertFromRegistryAsync(registryPath, "gfx-test");
        await fixture.Context.SaveChangesAsync();

        // 同一 group 再来一次（模拟 repo-registry.json 更新后重跑）
        var second = await svc.UpsertFromRegistryAsync(registryPath, "gfx-test");
        Assert.Equal("gfx-test", second.Id);
        Assert.Equal(2, second.Repos.Count); // 不应重复
    }

    [Fact]
    public async Task UpsertFromRegistryAsync_UnknownGroup_ThrowsWithAvailablePaths()
    {
        await using var fixture = await SqliteFixture.CreateAsync();
        var registryPath = TestRegistryJson.WriteToTempFile();

        var svc = new WorkspaceService(fixture.Context, NullLogger<WorkspaceService>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.UpsertFromRegistryAsync(registryPath, "non-existent-group"));
        Assert.Contains("non-existent-group", ex.Message);
        // 错误提示列出可寻址节点路径（含子组路径）
        Assert.Contains("gfx-test/client", ex.Message);
    }

    [Fact]
    public async Task MarkRunningThenSucceeded_UpdatesStatus()
    {
        await using var fixture = await SqliteFixture.CreateAsync();
        var registryPath = TestRegistryJson.WriteToTempFile();
        var svc = new WorkspaceService(fixture.Context, NullLogger<WorkspaceService>.Instance);
        await svc.UpsertFromRegistryAsync(registryPath, "gfx-test");

        await svc.MarkRunningAsync("gfx-test");
        var running = await svc.GetAsync("gfx-test");
        Assert.Equal(WorkspaceGroupStatus.Running, running!.LastRunStatus);
        Assert.NotNull(running.LastRunAt);

        await svc.MarkSucceededAsync("gfx-test");
        var ok = await svc.GetAsync("gfx-test");
        Assert.Equal(WorkspaceGroupStatus.Succeeded, ok!.LastRunStatus);
        Assert.Null(ok.LastRunError);
    }

    [Fact]
    public async Task MarkFailed_StoresTruncatedError()
    {
        await using var fixture = await SqliteFixture.CreateAsync();
        var registryPath = TestRegistryJson.WriteToTempFile();
        var svc = new WorkspaceService(fixture.Context, NullLogger<WorkspaceService>.Instance);
        await svc.UpsertFromRegistryAsync(registryPath, "gfx-test");

        var longError = new string('x', 3000); // > 2000 限制
        await svc.MarkFailedAsync("gfx-test", longError);

        var g = await svc.GetAsync("gfx-test");
        Assert.Equal(WorkspaceGroupStatus.Failed, g!.LastRunStatus);
        Assert.True(g.LastRunError!.Length <= 2000);
    }

    /// <summary>
    /// 真实 SQLite 文件库 fixture，用 EnsureCreated 直接从模型建表（绕过上游 migration 链）。
    /// </summary>
    private sealed class SqliteFixture : IAsyncDisposable
    {
        public string DbPath { get; }
        public SqliteDbContext DbContext { get; }
        public IContext Context => DbContext;

        private SqliteFixture(string dbPath, SqliteDbContext ctx)
        {
            DbPath = dbPath;
            DbContext = ctx;
        }

        public static async Task<SqliteFixture> CreateAsync()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"odw_ws_test_{Guid.NewGuid():N}.db");
            var options = new DbContextOptionsBuilder<SqliteDbContext>()
                .UseSqlite($"DataSource={dbPath}")
                .ConfigureWarnings(w => w.Ignore(
                    Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
                .Options;
            var ctx = new SqliteDbContext(options);
            await ctx.Database.EnsureCreatedAsync();
            return new SqliteFixture(dbPath, ctx);
        }

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { /* best-effort */ }
        }
    }
}
