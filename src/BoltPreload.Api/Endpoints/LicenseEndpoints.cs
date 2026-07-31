using BoltPreload.Application.Licensing;
using Microsoft.AspNetCore.Authorization;

namespace BoltPreload.Api.Endpoints;

/// <summary>
/// 授权管理端点（/api/license）：查机器码、查授权状态、上传 license.json。
/// 整组匿名——授权未装好前也要能访问（配合 LicenseGateMiddleware 白名单）。
/// </summary>
public static class LicenseEndpoints
{
    public static IEndpointRouteBuilder MapLicenseEndpoints(this IEndpointRouteBuilder app)
    {
        // License 接口设计上要在"还没登录、还没装授权"时也能用 — 用来获取机器码、查授权状态。
        // 因此整组匿名（也已在 LicenseGateMiddleware 白名单里）。
        var group = app.MapGroup("/api/license")
            .WithTags("License")
            .AllowAnonymous();

        group.MapGet("/machine-code", (ILicenseService service) => Results.Ok(new
        {
            machineCode = service.MachineCode,
            note = "把此机器码发给授权生成工具/供应商，用于生成离线 license.json。"
        }))
        .WithName("GetMachineCode")
        .WithSummary("获取本机机器码");

        // 匿名版：只回答"授权装好了没"——安装/排障时未登录也要能自检。
        // 客户名/授权号/容量等业务信息不对匿名暴露,走下面的已登录 detail。
        group.MapGet("/status", (ILicenseService service) =>
        {
            var s = service.GetStatus();
            return Results.Ok(new { s.Found, s.Valid, s.Expired, s.MachineCode, s.Warning });
        })
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
