using BoltPreload.Application.Devices;
using BoltPreload.Application.Licensing;

namespace BoltPreload.Api.Auth;

/// <summary>
/// 授权容量的预检与热重估。启动时 Program 按运行时现状评估一次；设备热增删/启停后必须重估——
/// 不重估的话 LicenseGate 拿的还是启动快照，热加超容量的设备后闸门不会合拢，
/// /api/runtime/devices 里的 withinLimits 也会和 overDeviceLimit 两个口径打架。
/// </summary>
public static class LicenseCapacity
{
    /// <summary>
    /// 设备新增/启用前的预检：会超容量就返回违规描述（端点回 409），可加返回 null。
    /// 通道数用"当前会话在用通道 + 新设备额定通道数"估算——新设备还没连上，只能按额定算，
    /// 实际连上后 <see cref="Reevaluate"/> 会按会话真值收口。
    /// </summary>
    public static string? CheckAdd(int currentDevices, int currentChannels, int addChannels, int? maxDevices, int? maxChannels)
    {
        if (maxDevices is int md && currentDevices + 1 > md)
            return $"设备数将达 {currentDevices + 1}，超过授权上限 {md}";
        if (maxChannels is int mc && currentChannels + addChannels > mc)
            return $"通道数将达 {currentChannels + addChannels}，超过授权上限 {mc}";
        return null;
    }

    /// <summary>按运行时现状重估执法状态（LicenseGate 据此拦写操作）。</summary>
    public static void Reevaluate(IDeviceConnectorManager manager, ILicenseService license, LicenseEnforcementState enforcement)
    {
        var status = license.GetStatus();
        enforcement.Set(manager.Runtimes.Count, manager.CountActiveChannels(), status.MaxDevices, status.MaxChannels);
    }
}
