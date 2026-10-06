using FireRakshak.FRMC.Core.Models;
using Microsoft.Extensions.Logging;

namespace FireRakshak.FRMC.Managers.ChannelsManager;

/// <summary>
/// Evaluates zone temperatures against defined thresholds and calculates rate of rise and zone deviations.
/// </summary>
public class ThresholdComparator
{
    private readonly ILogger<ThresholdComparator> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ThresholdComparator"/> class.
    /// </summary>
    /// <param name="logger">The logger for recording information.</param>
    public ThresholdComparator(ILogger<ThresholdComparator> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Evaluates if the maximum temperature threshold is breached for a zone.
    /// </summary>
    /// <param name="config">The zone configuration.</param>
    /// <param name="state">The current zone state.</param>
    /// <returns>True if the max temperature threshold is breached, false otherwise.</returns>
    public bool EvaluateMaxTemperature(ZoneConfiguration config, ZoneState state)
    {
        _logger.LogDebug("Evaluating max temperature for zone {ZoneId}. Current: {CurrentTemp}, Max: {MaxTemp}", state.ZoneId, state.MaxTemperature, config.MaxTemp);
        // TODO: Implement actual max temperature comparison logic
        return state.MaxTemperature >= config.MaxTemp;
    }

    /// <summary>
    /// Evaluates if the minimum temperature threshold is breached for a zone.
    /// </summary>
    /// <param name="config">The zone configuration.</param>
    /// <param name="state">The current zone state.</param>
    /// <returns>True if the min temperature threshold is breached, false otherwise.</returns>
    public bool EvaluateMinTemperature(ZoneConfiguration config, ZoneState state)
    {
        _logger.LogDebug("Evaluating min temperature for zone {ZoneId}. Current: {CurrentTemp}, Min: {MinTemp}", state.ZoneId, state.MinTemperature, config.MinTemp);
        return state.MinTemperature <= config.MinTemp;
    }

    /// <summary>
    /// Evaluates if the PRE-ALARM threshold is breached.
    /// PreAlarmTemp should always be slightly LESS than MaxTemp (configured in ZoneConfiguration).
    /// </summary>
    public bool EvaluatePreAlarm(ZoneConfiguration config, ZoneState state)
    {
        _logger.LogDebug(
            "Evaluating PRE-ALARM for zone {ZoneId}. CurrentMax: {CurrentTemp}, PreAlarm: {PreAlarmTemp}",
            state.ZoneId, state.MaxTemperature, config.PreAlarm);

        return state.MaxTemperature >= config.PreAlarm &&
               state.MaxTemperature < config.MaxTemp; // only in between
    }

    public bool EvaluateRateOfRise(ZoneConfiguration config, ZoneState state)
    {
        _logger.LogDebug(
            "Evaluating RoR for zone {Z}. RoR={RoR}, Threshold={Thr}",
            state.ZoneId, state.RateOfRise, config.RoRThreshold);

        return state.RateOfRise >= config.RoRThreshold;
    }

    // ⭐ NEW — Deviation Alarm Evaluation
    public bool EvaluateDeviation(ZoneConfiguration config, ZoneState state)
    {
        _logger.LogDebug(
            "Evaluating Deviation for zone {Z}. Dev={Dev}, Thr={Thr}",
            state.ZoneId, state.Deviation, config.DeviationThreshold);
        return state.Deviation > config.DeviationThreshold;

    }
}
