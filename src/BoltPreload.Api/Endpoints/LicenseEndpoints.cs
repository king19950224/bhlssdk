using BoltPreload.Application.Licensing;
using Microsoft.AspNetCore.Authorization;

namespace BoltPreload.Api.Endpoints;

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

        group.MapGet("/status", (ILicenseService service) =>
        {
            // 不直接暴露完整 Document（含 signature / 客户公司等），只返回核心字段。
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
        .WithName("GetLicenseStatus")
        .WithSummary("获取授权状态（脱敏；不含 signature / Document 全文）");

        return app;
    }
}
