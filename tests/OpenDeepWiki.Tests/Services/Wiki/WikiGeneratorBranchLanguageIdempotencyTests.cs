using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.Wiki;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Wiki;

/// <summary>
/// 翻译任务重跑幂等性：目标 BranchLanguage 已存在时必须复用，
/// 否则直接插入会撞 (RepositoryBranchId, LanguageCode) 唯一索引（PG 23505）。
/// </summary>
public class WikiGeneratorBranchLanguageIdempotencyTests : IDisposable
{
    private class TestDbContext : MasterDbContext
    {
        public TestDbContext(DbContextOptions<TestDbContext> options)
            : base(options)
        {
        }
    }

    private readonly TestDbContext _context;

    public WikiGeneratorBranchLanguageIdempotencyTests()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new TestDbContext(options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    private static BranchLanguage SeedBranchLanguage(TestDbContext context, string repositoryBranchId, string languageCode, bool isDeleted)
    {
        var language = new BranchLanguage
        {
            Id = Guid.NewGuid().ToString(),
            RepositoryBranchId = repositoryBranchId,
            LanguageCode = languageCode
        };

        if (isDeleted)
        {
            language.MarkAsDeleted();
        }

        context.BranchLanguages.Add(language);
        context.SaveChanges();
        return language;
    }

    [Fact]
    public async Task GetOrCreateTargetBranchLanguageAsync_WhenActiveRowExists_ReusesSameRow()
    {
        var repositoryBranchId = Guid.NewGuid().ToString();
        var existing = SeedBranchLanguage(_context, repositoryBranchId, "zh-cn", isDeleted: false);

        var result = await WikiGenerator.GetOrCreateTargetBranchLanguageAsync(
            _context, repositoryBranchId, "zh-CN", NullLogger<WikiGenerator>.Instance, CancellationToken.None);

        Assert.Same(existing, result);
        Assert.Single(_context.BranchLanguages);
    }

    [Fact]
    public async Task GetOrCreateTargetBranchLanguageAsync_WhenSoftDeletedRowExists_RevivesAndReuses()
    {
        var repositoryBranchId = Guid.NewGuid().ToString();
        var existing = SeedBranchLanguage(_context, repositoryBranchId, "zh-cn", isDeleted: true);

        var result = await WikiGenerator.GetOrCreateTargetBranchLanguageAsync(
            _context, repositoryBranchId, "zh-cn", NullLogger<WikiGenerator>.Instance, CancellationToken.None);

        Assert.Same(existing, result);
        Assert.False(result.IsDeleted);
        Assert.Single(_context.BranchLanguages);
    }

    [Fact]
    public async Task GetOrCreateTargetBranchLanguageAsync_WhenNoRowExists_CreatesNormalizedRow()
    {
        var repositoryBranchId = Guid.NewGuid().ToString();

        var result = await WikiGenerator.GetOrCreateTargetBranchLanguageAsync(
            _context, repositoryBranchId, "zh-CN", NullLogger<WikiGenerator>.Instance, CancellationToken.None);

        Assert.Equal(repositoryBranchId, result.RepositoryBranchId);
        Assert.Equal("zh-cn", result.LanguageCode);
        Assert.Single(_context.BranchLanguages);
    }
}
