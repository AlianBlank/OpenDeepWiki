using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;

namespace OpenDeepWiki.Endpoints;

/// <summary>
/// 数据导入端点：把 JSON 数据 upsert 进库（以 Id 为准，已存在则更新，不存在则插入）。
/// POST /api/import/all 接受 ExportEndpoints.ExportAllAsync 输出的格式。
/// POST /api/import/{table} 接受单表数组。
/// </summary>
public static class ImportEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static IEndpointRouteBuilder MapImportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/import")
            .WithTags("Export / Import")
            .RequireAuthorization("AdminOnly");

        group.MapPost("/all", ImportAllAsync)
            .WithSummary("按全量 JSON 导入（upsert）")
            .WithDescription("接受 /api/export/all 的输出格式。以 Id 为准做 upsert。需要 Admin 权限。");

        group.MapPost("/{table}", ImportTableAsync)
            .WithSummary("按单表 JSON 数组导入（upsert）")
            .WithDescription("接受 /api/export/{table} 的 data 字段（数组）。需要 Admin 权限。");

        return app;
    }

    public sealed class ImportAllPayload
    {
        public List<WorkspaceRepoGroup>? WorkspaceRepoGroups { get; set; }
        public List<RepoRef>? RepoRefs { get; set; }
        public List<SystemSetting>? SystemSettings { get; set; }
        public List<AiProviderConfig>? AiProviderConfigs { get; set; }
        public List<AiModelConfig>? AiModelConfigs { get; set; }
        public List<ModelConfig>? ModelConfigs { get; set; }
        public List<McpProvider>? McpProviders { get; set; }
        public List<ChatAssistantConfig>? ChatAssistantConfigs { get; set; }
    }

    private static async Task<IResult> ImportAllAsync(
        ImportAllPayload payload,
        IContext context,
        CancellationToken cancellationToken)
    {
        var stats = new Dictionary<string, int>();

        if (payload.WorkspaceRepoGroups is { Count: > 0 })
            stats["workspaceRepoGroups"] = await UpsertRangeAsync(
                context.WorkspaceRepoGroups, payload.WorkspaceRepoGroups, context, cancellationToken);

        if (payload.RepoRefs is { Count: > 0 })
            stats["repoRefs"] = await UpsertRangeAsync(
                context.RepoRefs, payload.RepoRefs, context, cancellationToken);

        if (payload.SystemSettings is { Count: > 0 })
            stats["systemSettings"] = await UpsertRangeAsync(
                context.SystemSettings, payload.SystemSettings, context, cancellationToken);

        if (payload.AiProviderConfigs is { Count: > 0 })
            stats["aiProviderConfigs"] = await UpsertRangeAsync(
                context.AiProviderConfigs, payload.AiProviderConfigs, context, cancellationToken);

        if (payload.AiModelConfigs is { Count: > 0 })
            stats["aiModelConfigs"] = await UpsertRangeAsync(
                context.AiModelConfigs, payload.AiModelConfigs, context, cancellationToken);

        if (payload.ModelConfigs is { Count: > 0 })
            stats["modelConfigs"] = await UpsertRangeAsync(
                context.ModelConfigs, payload.ModelConfigs, context, cancellationToken);

        if (payload.McpProviders is { Count: > 0 })
            stats["mcpProviders"] = await UpsertRangeAsync(
                context.McpProviders, payload.McpProviders, context, cancellationToken);

        if (payload.ChatAssistantConfigs is { Count: > 0 })
            stats["chatAssistantConfigs"] = await UpsertRangeAsync(
                context.ChatAssistantConfigs, payload.ChatAssistantConfigs, context, cancellationToken);

        return Results.Ok(new { success = true, imported = stats });
    }

    private static async Task<IResult> ImportTableAsync(
        string table,
        HttpContext httpContext,
        IContext context,
        CancellationToken cancellationToken)
    {
        var body = await JsonSerializer.DeserializeAsync<JsonElement>(
            httpContext.Request.Body, JsonOptions);
        if (body.ValueKind == JsonValueKind.Undefined)
            return Results.BadRequest(new { error = true, message = "请求体必须是 JSON" });

        var count = table.ToLowerInvariant() switch
        {
            "workspacerepogroups" or "workspace_groups"
                => await UpsertJsonRangeAsync<WorkspaceRepoGroup>(context.WorkspaceRepoGroups, body, context, cancellationToken),
            "reporefs" or "repo_refs"
                => await UpsertJsonRangeAsync<RepoRef>(context.RepoRefs, body, context, cancellationToken),
            "systemsettings"
                => await UpsertJsonRangeAsync<SystemSetting>(context.SystemSettings, body, context, cancellationToken),
            "aiproviderconfigs"
                => await UpsertJsonRangeAsync<AiProviderConfig>(context.AiProviderConfigs, body, context, cancellationToken),
            "aimodelconfigs"
                => await UpsertJsonRangeAsync<AiModelConfig>(context.AiModelConfigs, body, context, cancellationToken),
            "modelconfigs"
                => await UpsertJsonRangeAsync<ModelConfig>(context.ModelConfigs, body, context, cancellationToken),
            "mcpproviders"
                => await UpsertJsonRangeAsync<McpProvider>(context.McpProviders, body, context, cancellationToken),
            "chatassistantconfigs"
                => await UpsertJsonRangeAsync<ChatAssistantConfig>(context.ChatAssistantConfigs, body, context, cancellationToken),
            _ => -1
        };

        if (count < 0)
            return Results.NotFound(new
            {
                error = true,
                message = $"不支持的表：{table}"
            });

        return Results.Ok(new { success = true, table, imported = count });
    }

    private static async Task<int> UpsertRangeAsync<TEntity>(
        DbSet<TEntity> set,
        IEnumerable<TEntity> items,
        IContext context,
        CancellationToken cancellationToken) where TEntity : class
    {
        // IContext 不暴露 Entry()，运行时实际是 DbContext，cast 后才能用 CurrentValues.SetValues
        var dbCtx = context as DbContext;
        var count = 0;
        foreach (var item in items)
        {
            var idProp = typeof(TEntity).GetProperty("Id")?.GetValue(item)?.ToString();
            if (idProp is null)
            {
                await set.AddAsync(item, cancellationToken);
            }
            else
            {
                var existing = await set.FindAsync([idProp], cancellationToken);
                if (existing is null)
                {
                    await set.AddAsync(item, cancellationToken);
                }
                else if (dbCtx is not null)
                {
                    // 用 CurrentValues 拷字段，避免两个不同实例同 key 被 tracker 同时跟踪
                    dbCtx.Entry(existing).CurrentValues.SetValues(item);
                }
            }
            count++;
        }
        await context.SaveChangesAsync(cancellationToken);
        return count;
    }

    private static async Task<int> UpsertJsonRangeAsync<TEntity>(
        DbSet<TEntity> set,
        JsonElement body,
        IContext context,
        CancellationToken cancellationToken) where TEntity : class
    {
        // body 可以是数组（直接是表的行），也可以是 { data: [...] }
        JsonElement array = body.ValueKind == JsonValueKind.Array
            ? body
            : (body.TryGetProperty("data", out var d) ? d : body);

        if (array.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("请求体必须是 JSON 数组，或 { data: [...] }", nameof(body));

        var items = array.Deserialize<List<TEntity>>(JsonOptions) ?? new List<TEntity>();
        return await UpsertRangeAsync(set, items, context, cancellationToken);
    }
}
