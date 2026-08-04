using BoltPreload.Application.Licensing;
using Microsoft.AspNetCore.Authorization;

namespace BoltPreload.Api.Endpoints;

/// <summary>
/// 授权管理端点（/api/license）：查机器码、查授权状态、查授权详情。
/// <para>
/// 匿名开口<b>逐个端点</b>给，不能加在组上：<c>AllowAnonymous</c> 只要出现在端点元数据里，
/// 授权中间件就整个短路，同一端点上的 <c>RequireAuthorization</c> 不论写在前写在后都不再生效。
/// 曾经这里是整组匿名 + detail 单独 RequireAuthorization，看着像收紧了，
/// 实际那句是死代码，客户名/授权号/容量上限对匿名敞开。
/// </para>
/// </summary>
public static class LicenseEndpoints
{
    public static IEndpointRouteBuilder MapLicenseEndpoints(this IEndpointRouteBuilder app)
    {
        // 组上不设授权口径。machine-code 与 status 各自显式 AllowAnonymous：
        // 装机时"还没登录、还没装授权"就得靠这两个自检（也已在 LicenseGateMiddleware 白名单里）。
        var group = app.MapGroup("/api/license")
            .WithTags("License");

        group.MapGet("/machine-code", (ILicenseService service) => Results.Ok(new
        {
            machineCode = service.MachineCode,
            note = "把此机器码发给授权生成工具/供应商，用于生成离线 license.json。"
        }))
        .AllowAnonymous()
        .WithName("GetMachineCode")
        .WithSummary("获取本机机器码");

        // 匿名版：只回答"授权装好了没"——安装/排障时未登录也要能自检。
        // 客户名/授权号/容量等业务信息不对匿名暴露,走下面的已登录 detail。
        group.MapGet("/status", (ILicenseService service) =>
        {
            var s = service.GetStatus();
            return Results.Ok(new { s.Found, s.Valid, s.Expired, s.MachineCode, s.Warning });
        })
        .AllowAnonymous()
        .WithName("GetLicenseStatus")
        .WithSummary("获取授权状态（匿名精简版：仅有效性与机器码）");

        // 登录版：完整脱敏字段（不含 signature / Document 全文）
        group.MapGet("/detail", (ILicenseService service) =>
        {
            var s = service.GetStatus();
            return Results.Ok(new
            {
                s.Found,
                s.Valid,
                s.Expired,
                s.MachineCode,
                s.LicensedTo,
                s.LicenseId,
                s.ExpiresAt,
                s.MaxDevices,
                s.MaxChannels,
                s.Modules,
                s.Warning
            });
        })
        .RequireAuthorization(Auth.AuthPolicies.LoggedIn)
        .WithName("GetLicenseDetail")
        .WithSummary("获取授权详情（需登录；不含 signature / Document 全文）");

        return app;
    }
}
