using BoltPreload.Application.Alarm;

namespace BoltPreload.Tests.Alarm;

/// <summary>
/// 测试用规则提供者：固定一套阈值，可选按 boltId 覆盖。RefreshAsync 无操作。
/// 生产实现（PostgresAlarmRuleProvider）的三级解析另有对真库的集成测试。
/// </summary>
internal sealed class FixedAlarmRuleProvider : IAlarmRuleProvider
{
    private readonly StaticThresholdAlarmOptions _default;
    private readonly IReadOnlyDictionary<long, StaticThresholdAlarmOptions>? _byBolt;

    public FixedAlarmRuleProvider(
        StaticThresholdAlarmOptions? defaultOptions = null,
        IReadOnlyDictionary<long, StaticThresholdAlarmOptions>? byBolt = null)
    {
        _default = defaultOptions ?? new StaticThresholdAlarmOptions();
        _byBolt = byBolt;
    }

    public StaticThresholdAlarmOptions Resolve(long boltId) =>
        _byBolt is not null && _byBolt.TryGetValue(boltId, out var opt) ? opt : _default;

    public Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;
}
