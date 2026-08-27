using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services;
using OpenDeepWiki.Services.Translation;
using OpenDeepWiki.Tests.Chat.Sessions;
using Xunit;

namespace OpenDeepWiki.Tests.Services;

public class TranslationWorkerTests
{
    [Fact]
    public async Task ReclaimOrphanedTasksAsync_ResetsProcessingToPending_LeavesOthersUntouched()
    {
        using var context = CreateContext();
        var processing = SeedTask(context, TranslationTaskStatus.Processing, isDeleted: false);
        var processingDeleted = SeedTask(context, TranslationTaskStatus.Processing, isDeleted: true);
        var pending = SeedTask(context, TranslationTaskStatus.Pending, isDeleted: false);
        var completed = SeedTask(context, TranslationTaskStatus.Completed, isDeleted: false);
        await context.SaveChangesAsync();

        var worker = CreateWorker(context);

        await InvokeReclaimAsync(worker);

        Assert.Equal(TranslationTaskStatus.Pending, await GetStatusAsync(context, processing));
        Assert.Equal(TranslationTaskStatus.Processing, await GetStatusAsync(context, processingDeleted));
        Assert.Equal(TranslationTaskStatus.Pending, await GetStatusAsync(context, pending));
        Assert.Equal(TranslationTaskStatus.Completed, await GetStatusAsync(context, completed));
    }

    [Fact]
    public async Task MarkAsCompletedAsync_ClearsLeftoverErrorMessage()
    {
        using var context = CreateContext();
        var task = new TranslationTask
        {
            Id = Guid.NewGuid().ToString(),
            RepositoryId = "repo",
            RepositoryBranchId = "branch",
            SourceBranchLanguageId = "source",
            TargetLanguageCode = "ko",
            Status = TranslationTaskStatus.Processing,
            ErrorMessage = "Failed to pull repository after 3 attempts"
        };
        context.TranslationTasks.Add(task);
        await context.SaveChangesAsync();

        var service = new TranslationService(context, NullLogger<TranslationService>.Instance);

        await service.MarkAsCompletedAsync(task.Id);

        var reloaded = await context.TranslationTasks.SingleAsync(t => t.Id == task.Id);
        Assert.Equal(TranslationTaskStatus.Completed, reloaded.Status);
        Assert.Null(reloaded.ErrorMessage);
    }

    private static async Task<TranslationTaskStatus> GetStatusAsync(TestDbContext context, TranslationTask task)
        => await context.TranslationTasks.AsNoTracking().Where(t => t.Id == task.Id).Select(t => t.Status).SingleAsync();

    private static TranslationTask SeedTask(TestDbContext context, TranslationTaskStatus status, bool isDeleted)
    {
        var task = new TranslationTask
        {
            Id = Guid.NewGuid().ToString(),
            RepositoryId = "repo",
            RepositoryBranchId = "branch",
            SourceBranchLanguageId = "source",
            TargetLanguageCode = Guid.NewGuid().ToString()[..8],
            Status = status,
            IsDeleted = isDeleted
        };
        context.TranslationTasks.Add(task);
        return task;
    }

    private static TestDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new TestDbContext(options);
    }

    private static TranslationWorker CreateWorker(TestDbContext context)
    {
        var provider = new Mock<IServiceProvider>();
        provider
            .Setup(p => p.GetService(typeof(OpenDeepWiki.EFCore.IContext)))
            .Returns(context);

        var scope = new Mock<IServiceScope>();
        scope.SetupGet(s => s.ServiceProvider).Returns(provider.Object);

        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        var configuration = new ConfigurationBuilder().Build();
        var guard = new GenerationWindowGuard(configuration, NullLogger<GenerationWindowGuard>.Instance);
        var breaker = new AiQuotaCircuitBreaker(configuration, NullLogger<AiQuotaCircuitBreaker>.Instance);

        return new TranslationWorker(
            scopeFactory.Object,
            NullLogger<TranslationWorker>.Instance,
            guard,
            breaker);
    }

    private static async Task InvokeReclaimAsync(TranslationWorker worker)
    {
        var method = typeof(TranslationWorker).GetMethod(
            "ReclaimOrphanedTasksAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        await (Task)method.Invoke(worker, new object[] { CancellationToken.None })!;
    }
}
