using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.Workspaces;

namespace OpenDeepWiki.Endpoints;

/// <summary>
/// 工作区仓组相关接口：CRUD + 从 repo-registry.json bootstrap + 触发运行。
/// </summary>
public static class WorkspaceEndpoints
{
    public static IEndpointRouteBuilder MapWorkspaceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/workspaces")
            .WithTags("Workspaces")
            .RequireAuthorization();

        // 列出全部组
        group.MapGet("/", async (IWorkspaceService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListAsync(ct)))
            .WithSummary("列出所有工作区仓组");

        // 取单个组
        group.MapGet("/{groupId}", async (string groupId, IWorkspaceService svc, CancellationToken ct) =>
        {
            var g = await svc.GetAsync(groupId, ct);
            return g is null ? Results.NotFound(new { error = true, message = $"Group '{groupId}' not found" }) : Results.Ok(g);
        }).WithSummary("取单个工作区仓组");

        // 从 repo-registry.json 导入 / 更新
        group.MapPost("/bootstrap", async (
            BootstrapRequest req,
            IWorkspaceService svc,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.RegistryPath))
                return Results.BadRequest(new { error = true, message = "registryPath 必填" });
            if (string.IsNullOrWhiteSpace(req.GroupId))
                return Results.BadRequest(new { error = true, message = "groupId 必填" });

            try
            {
                var group = await svc.UpsertFromRegistryAsync(req.RegistryPath, req.GroupId, ct);
                return Results.Ok(new { success = true, data = group });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = true, message = ex.Message });
            }
        }).WithSummary("从 repo-registry.json 导入或更新工作区仓组")
          .RequireAuthorization("AdminOnly");

        // 触发运行（标记为 Running，由 WorkspaceProcessingWorker 接管推进）
        group.MapPost("/{groupId}/run", async (string groupId, IWorkspaceService svc, CancellationToken ct) =>
        {
            var g = await svc.GetAsync(groupId, ct);
            if (g is null) return Results.NotFound(new { error = true, message = $"Group '{groupId}' not found" });

            await svc.MarkRunningAsync(groupId, ct);
            return Results.Ok(new { success = true, message = "Group marked running; WorkspaceProcessingWorker will pick it up." });
        }).WithSummary("触发工作区仓组运行")
          .RequireAuthorization("AdminOnly");

        // 删除组（软删除）
        group.MapDelete("/{groupId}", async (string groupId, IContext context, CancellationToken ct) =>
        {
            var g = await context.WorkspaceRepoGroups.FindAsync([groupId], ct);
            if (g is null) return Results.NotFound(new { error = true, message = $"Group '{groupId}' not found" });
            g.MarkAsDeleted();
            await context.SaveChangesAsync(ct);
            return Results.Ok(new { success = true });
        }).WithSummary("软删除工作区仓组")
          .RequireAuthorization("AdminOnly");

        return app;
    }
}

public sealed class BootstrapRequest
{
    /// <summary>
    /// repo-registry.json 的绝对路径
    /// </summary>
    public required string RegistryPath { get; set; }

    /// <summary>
    /// 要 bootstrap 的组 Id（必须在 registry 文件中存在）
    /// </summary>
    public required string GroupId { get; set; }
}
