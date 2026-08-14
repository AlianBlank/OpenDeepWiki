using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenDeepWiki.EFCore;

namespace OpenDeepWiki.Endpoints;

/// <summary>
/// 数据导出端点：把指定表（或全表）导出为 JSON。
/// 用例：备份、迁移、跨实例同步。GET /api/export/all 返回全部表的合并 JSON。
/// </summary>
public static class ExportEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static IEndpointRouteBuilder MapExportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/export")
            .WithTags("Export / Import")
            .RequireAuthorization("AdminOnly");

        group.MapGet("/all", ExportAllAsync)
            .WithSummary("导出全部表数据为 JSON")
            .WithDescription("返回所有支持的表合并的 JSON 文档。需要 Admin 权限。");

        group.MapGet("/{table}", ExportTableAsync)
            .WithSummary("导出指定表数据为 JSON")
            .WithDescription("返回单张表的完整数据。需要 Admin 权限。");

        return app;
    }

    private static async Task<IResult> ExportAllAsync(IContext context, CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["workspaceRepoGroups"] = await context.WorkspaceRepoGroups.AsNoTracking().ToListAsync(cancellationToken),
            ["repoRefs"] = await context.RepoRefs.AsNoTracking().ToListAsync(cancellationToken),
            ["systemSettings"] = await context.SystemSettings.AsNoTracking().ToListAsync(cancellationToken),
            ["aiProviderConfigs"] = await context.AiProviderConfigs.AsNoTracking().ToListAsync(cancellationToken),
            ["aiModelConfigs"] = await context.AiModelConfigs.AsNoTracking().ToListAsync(cancellationToken),
            ["modelConfigs"] = await context.ModelConfigs.AsNoTracking().ToListAsync(cancellationToken),
            ["mcpProviders"] = await context.McpProviders.AsNoTracking().ToListAsync(cancellationToken),
            ["chatAssistantConfigs"] = await context.ChatAssistantConfigs.AsNoTracking().ToListAsync(cancellationToken),
            ["exportedAt"] = DateTime.UtcNow
        };

        return Results.Ok(new { success = true, data = payload });
    }

    private static async Task<IResult> ExportTableAsync(string table, IContext context, CancellationToken cancellationToken)
    {
        object? rows = table.ToLowerInvariant() switch
        {
            "workspacerepogroups" or "workspace_groups" => await context.WorkspaceRepoGroups.AsNoTracking().ToListAsync(cancellationToken),
            "reporefs" or "repo_refs" => await context.RepoRefs.AsNoTracking().ToListAsync(cancellationToken),
            "systemsettings" => await context.SystemSettings.AsNoTracking().ToListAsync(cancellationToken),
            "aiproviderconfigs" => await context.AiProviderConfigs.AsNoTracking().ToListAsync(cancellationToken),
            "aimodelconfigs" => await context.AiModelConfigs.AsNoTracking().ToListAsync(cancellationToken),
            "modelconfigs" => await context.ModelConfigs.AsNoTracking().ToListAsync(cancellationToken),
            "mcpproviders" => await context.McpProviders.AsNoTracking().ToListAsync(cancellationToken),
            "chatassistantconfigs" => await context.ChatAssistantConfigs.AsNoTracking().ToListAsync(cancellationToken),
            _ => null
        };

        if (rows is null)
            return Results.NotFound(new
            {
                error = true,
                message = $"不支持的表：{table}。支持：workspaceRepoGroups / repoRefs / systemSettings / aiProviderConfigs / aiModelConfigs / modelConfigs / mcpProviders / chatAssistantConfigs"
            });

        return Results.Ok(new { success = true, table, count = ((System.Collections.ICollection)rows).Count, data = rows });
    }
}
