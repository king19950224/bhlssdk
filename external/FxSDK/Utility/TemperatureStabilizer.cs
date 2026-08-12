using FxSDK.Core;

namespace FxSDK.Utility;

public static class TemperatureStabilizer
{
    private const string StatePrefix = "TEMP-TAU|";

    public static decimal ResolveTauTemp(IoSession session, ushort address, ushort channel, decimal temperature,
        DateTime sampleTime, decimal tauSeconds)
    {
        var key = GetStateKey(address, channel);
        var state = SessionContext.GetSessionAttribute<TemperatureStabilizerState?>(session, key);
        if (tauSeconds <= 0 || state == null)
        {
            StoreState(session, key, temperature, sampleTime);
            return temperature;
        }

        var dtSeconds = (sampleTime - state.SampleTime).TotalSeconds;
        if (dtSeconds <= 0)
        {
            return state.EffectiveTemp;
        }

        var tau = (double)tauSeconds;
        if (dtSeconds > Math.Max(tau * 5d, 600d))
        {
            StoreState(session, key, temperature, sampleTime);
            return temperature;
        }

        var alpha = 1d - Math.Exp(-dtSeconds / tau);
        var effectiveTemp = state.EffectiveTemp + (decimal)alpha * (temperature - state.EffectiveTemp);
        StoreState(session, key, effectiveTemp, sampleTime);
        return effectiveTemp;
    }

    private static void StoreState(IoSession session, string key, decimal effectiveTemp, DateTime sampleTime)
    {
        SessionContext.SetSessionAttribute(session, key, new TemperatureStabilizerState
        {
            EffectiveTemp = effectiveTemp,
            SampleTime = sampleTime
        });
    }

    private static string GetStateKey(ushort address, ushort channel)
    {
        return $"{StatePrefix}{address}|{channel}";
    }

    private sealed class TemperatureStabilizerState
    {
        public decimal EffectiveTemp { get; init; }
        public DateTime SampleTime { get; init; }
    }
}
