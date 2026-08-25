using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.Wiki;

namespace OpenDeepWiki.Services.Repositories;

public interface IRepositoryBranchProcessor
{
    Task<string?> ProcessBranchAsync(
        IContext context,
        Repository repository,
        RepositoryBranch branch,
        string? generationTaskId,
        bool forceFullGeneration,
        CancellationToken cancellationToken = default);
}

public sealed class RepositoryBranchProcessor(
    IRepositoryAnalyzer repositoryAnalyzer,
    IWikiGenerator wikiGenerator,
    IRepositorySkillMarkdownBuilder? skillMarkdownBuilder,
    IRepositoryScanPlanResolver? scanPlanResolver,
    IProcessingLogService? processingLogService,
    ILogger<RepositoryBranchProcessor> logger) : IRepositoryBranchProcessor
{
    public async Task<string?> ProcessBranchAsync(
        IContext context,
        Repository repository,
        RepositoryBranch branch,
        string? generationTaskId,
        bool forceFullGeneration,
        CancellationToken cancellationToken = default)
    {
        var branchStopwatch = Stopwatch.StartNew();
        logger.LogInformation(
            "Starting branch processing. BranchId: {BranchId}, Branch: {BranchName}, Repository: {Org}/{Repo}, LastCommitId: {LastCommitId}, TaskId: {TaskId}, ForceFull: {ForceFull}",
            branch.Id, branch.BranchName, repository.OrgName, repository.RepoName, branch.LastCommitId ?? "none", generationTaskId ?? "none", forceFullGeneration);

        var wikiGeneratorImpl = wikiGenerator as WikiGenerator;
        if (wikiGeneratorImpl is not null)
        {
            wikiGeneratorImpl.SetCurrentRepository(repository.Id, $"{repository.OrgName}/{repository.RepoName}");
            wikiGeneratorImpl.SetCurrentGenerationContext(branch.Id, generationTaskId);
        }

        await LogAsync(repository.Id, branch.Id, generationTaskId, ProcessingStep.Workspace,
            $"Preparing workspace, branch: {branch.BranchName}", cancellationToken);

        var previousCommitId = forceFullGeneration ? null : branch.LastCommitId;
        var workspace = await repositoryAnalyzer.PrepareWorkspaceAsync(
            repository,
            branch.BranchName,
            previousCommitId,
            cancellationToken);

        try
        {
            await ResolveScanPlanAsync(context, repository, workspace.WorkingDirectory, branch.Id, generationTaskId, cancellationToken);

            await LogAsync(repository.Id, branch.Id, generationTaskId, ProcessingStep.Workspace,
                $"Workspace ready, Commit: {workspace.CommitId[..Math.Min(7, workspace.CommitId.Length)]}", cancellationToken);

            if (string.IsNullOrEmpty(repository.PrimaryLanguage))
            {
                var detectedLanguage = await repositoryAnalyzer.DetectPrimaryLanguageAsync(workspace, cancellationToken);
                if (!string.IsNullOrEmpty(detectedLanguage))
                {
                    repository.PrimaryLanguage = detectedLanguage;
                    context.Repositories.Update(repository);
                    await context.SaveChangesAsync(cancellationToken);

                    await LogAsync(repository.Id, branch.Id, generationTaskId, ProcessingStep.Workspace,
                        $"Primary language detected: {detectedLanguage}", cancellationToken);
                }
            }

            var languages = await context.BranchLanguages
                .Where(l => l.RepositoryBranchId == branch.Id && !l.IsDeleted)
                .ToListAsync(cancellationToken);

            if (languages.Count == 0)
            {
                logger.LogWarning(
                    "No languages found for branch. BranchId: {BranchId}, Branch: {BranchName}",
                    branch.Id, branch.BranchName);
                return workspace.CommitId;
            }

            var isIncremental = !forceFullGeneration &&
                                workspace.IsIncremental &&
                                workspace.PreviousCommitId != workspace.CommitId;

            string[]? changedFiles = null;
            if (isIncremental)
            {
                changedFiles = await repositoryAnalyzer.GetChangedFilesAsync(
                    workspace,
                    workspace.PreviousCommitId,
                    workspace.CommitId,
                    cancellationToken);
            }

            foreach (var language in languages)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await ProcessLanguageAsync(
                    context,
                    repository,
                    branch,
                    workspace,
                    language,
                    isIncremental,
                    changedFiles,
                    cancellationToken);
            }

            branch.LastCommitId = workspace.CommitId;
            branch.LastProcessedAt = DateTime.UtcNow;
            context.RepositoryBranches.Update(branch);
            await context.SaveChangesAsync(cancellationToken);

            branchStopwatch.Stop();
            logger.LogInformation(
                "Branch processing completed. BranchId: {BranchId}, Branch: {BranchName}, CommitId: {CommitId}, Duration: {Duration}ms",
                branch.Id, branch.BranchName, workspace.CommitId, branchStopwatch.ElapsedMilliseconds);

            return workspace.CommitId;
        }
        finally
        {
            if (wikiGeneratorImpl is not null)
            {
                wikiGeneratorImpl.SetCurrentGenerationContext(null, null);
            }

            await repositoryAnalyzer.CleanupWorkspaceAsync(workspace, cancellationToken);
        }
    }

    private async Task ResolveScanPlanAsync(
        IContext context,
        Repository repository,
        string workingDirectory,
        string branchId,
        string? generationTaskId,
        CancellationToken cancellationToken)
    {
        if (scanPlanResolver is null)
        {
            return;
        }

        var scanPlan = await scanPlanResolver.ResolveAndEnsureAsync(
            context,
            repository,
            workingDirectory,
            cancellationToken);

        await LogAsync(
            repository.Id,
            branchId,
            generationTaskId,
            ProcessingStep.Workspace,
            $"Resolved scan plan: {scanPlan.Source}, directoryDepth={scanPlan.DirectoryTreeDepth}, fileDepth={scanPlan.FileListDepth}, maxNodes={scanPlan.MaxTreeNodes}, maxFilesPerDirectory={scanPlan.MaxFilesPerDirectory}, maxTotalFiles={scanPlan.MaxTotalFiles}, profileHash={scanPlan.ProfileHash ?? "none"}",
            cancellationToken);
    }

    private async Task ProcessLanguageAsync(
        IContext context,
        Repository repository,
        RepositoryBranch branch,
        RepositoryWorkspace workspace,
        BranchLanguage language,
        bool isIncremental,
        string[]? changedFiles,
        CancellationToken cancellationToken)
    {
        // ponytail: 临时测试开关——只重生成指定语言的目录(验证目录提示词调整效果),环境变量未设置时零影响
        var catalogOnlyLanguages = Environment.GetEnvironmentVariable("ODW_CATALOG_ONLY_LANGUAGES");
        if (!string.IsNullOrWhiteSpace(catalogOnlyLanguages))
        {
            var enabledLanguages = catalogOnlyLanguages
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim().ToLowerInvariant())
                .ToHashSet();

            if (!enabledLanguages.Contains(language.LanguageCode.ToLowerInvariant()))
            {
                await LogAsync(
                    repository.Id,
                    branch.Id,
                    generationTaskId: null,
                    ProcessingStep.Catalog,
                    $"Skipping language {language.LanguageCode}: ODW_CATALOG_ONLY_LANGUAGES active",
                    cancellationToken);
                return;
            }

            await wikiGenerator.GenerateCatalogAsync(workspace, language, cancellationToken);
            await LogAsync(
                repository.Id,
                branch.Id,
                generationTaskId: null,
                ProcessingStep.Catalog,
                $"ODW_CATALOG_ONLY_LANGUAGES active: catalog regenerated, skipping documents for {language.LanguageCode}",
                cancellationToken);
            return;
        }

        // ponytail: 临时测试开关——目录已定稿时只重生成指定语言的内容(验证内容提示词)
        var contentOnlyLanguages = Environment.GetEnvironmentVariable("ODW_CONTENT_ONLY_LANGUAGES");
        if (!string.IsNullOrWhiteSpace(contentOnlyLanguages))
        {
            var enabledContentLanguages = contentOnlyLanguages
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim().ToLowerInvariant())
                .ToHashSet();

            if (!enabledContentLanguages.Contains(language.LanguageCode.ToLowerInvariant()))
            {
                return;
            }

            await LogAsync(
                repository.Id,
                branch.Id,
                generationTaskId: null,
                ProcessingStep.Content,
                $"ODW_CONTENT_ONLY_LANGUAGES active: skipping catalog, regenerating documents for {language.LanguageCode}",
                cancellationToken);
            await wikiGenerator.GenerateDocumentsAsync(workspace, language, cancellationToken);
            return;
        }

        if (isIncremental && changedFiles != null && changedFiles.Length > 0)
        {
            await wikiGenerator.IncrementalUpdateAsync(workspace, language, changedFiles, cancellationToken);
        }
        else
        {
            await wikiGenerator.GenerateCatalogAsync(workspace, language, cancellationToken);
            await wikiGenerator.GenerateDocumentsAsync(workspace, language, cancellationToken);
        }

        if (repository.GenerateSkill && skillMarkdownBuilder is not null)
        {
            await skillMarkdownBuilder.RefreshSkillMarkdownAsync(
                context,
                repository,
                branch,
                language,
                cancellationToken);
        }
    }

    private Task LogAsync(
        string repositoryId,
        string branchId,
        string? generationTaskId,
        ProcessingStep step,
        string message,
        CancellationToken cancellationToken)
    {
        return processingLogService is null
            ? Task.CompletedTask
            : processingLogService.LogAsync(
                repositoryId,
                branchId,
                generationTaskId,
                step,
                message,
                cancellationToken: cancellationToken);
    }
}
