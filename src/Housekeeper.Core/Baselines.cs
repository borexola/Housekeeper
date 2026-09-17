namespace Housekeeper.Core;

/// <summary>
/// What a numeric baseline will and will not support a verdict about.
///
/// A robust z-score answers one question — how far is this reading from the middle, measured in units of how
/// much this sensor usually wobbles — and it answers it perfectly well. The trouble is that it is only the
/// last of several questions, and on its own it fires constantly on readings no person would call surprising.
/// Everything here is one of the earlier questions, kept separate from the detector because each is a plain
/// predicate over an array of numbers and is worth being able to test as one.
/// </summary>
public static class Baselines
{
    /// <summary>
    /// A baseline that only ever moves one way, so its middle is behind its newest reading by construction.
    ///
    /// Energy totals, data counters, disk usage, uptime and a draining battery are all ratchets, and a z-score
    /// over one is not a measurement of anything: the median trails the current value permanently, the gap
    /// grows on its own, and the moment it clears the threshold the finding is raised and can never close. Six
    /// of the first nineteen findings on a real house were this, including "6.54 kWh, well outside its usual
    /// range, usually near 6.42 kWh" — which is a meter doing exactly what a meter does.
    ///
    /// Judged from the numbers rather than from <c>state_class</c> so it also covers the ones Home Assistant
    /// does not label: disk-used is reported as a measurement, and a battery's voltage is a ratchet pointing
    /// down. A few steps the other way are expected — a counter resets, a battery reads high after a cold
    /// morning — so this asks whether the moves are overwhelmingly one-directional, not perfectly so.
    /// </summary>
    public const double RatchetShare = 0.9;

    /// <summary>Moves needed before <see cref="IsRatchet"/> will call a direction at all.</summary>
    private const int RatchetMoves = 6;

    public static bool IsRatchet(IReadOnlyList<double> values)
    {
        int up = 0, down = 0;

        for (var i = 1; i < values.Count; i++)
        {
            if (values[i] > values[i - 1]) up++;
            else if (values[i] < values[i - 1]) down++;
        }

        var moves = up + down;
        if (moves < RatchetMoves) return false;

        return Math.Max(up, down) / (double)moves >= RatchetShare;
    }

    /// <summary>The span the baseline actually covered, which is a different thing from how many rows it has.</summary>
    public static TimeSpan Span(IReadOnlyList<StateSample> samples) =>
        samples.Count < 2
            ? TimeSpan.Zero
            : samples.Max(s => s.ChangedUtc) - samples.Min(s => s.ChangedUtc);

    /// <summary>The widest this baseline was ever seen to go, which is where a safe threshold has to sit outside.</summary>
    public static (double Min, double Max) Envelope(IReadOnlyList<double> values) =>
        values.Count == 0 ? (0, 0) : (values.Min(), values.Max());

    /// <summary>
    /// How much of the baseline sits within <paramref name="window"/> of <paramref name="value"/>.
    ///
    /// The test for "this is somewhere this sensor already lives". A plug, a pump, an HRV or a disk is not one
    /// distribution but two or three — off, low, full — and a median lands in whichever mode dominates, which
    /// leaves every other mode permanently scoring as an outlier. A real house reported its heat-recovery
    /// ventilator at 71.8 W against a usual 173.75 W, which is not a fault, it is the fan on a lower speed.
    ///
    /// Deliberately a small fraction: a mode the device visits one time in twenty is still a mode, while
    /// something genuinely new has no neighbours at all.
    /// </summary>
    public static double NearbyFraction(IReadOnlyList<double> values, double value, double window)
    {
        if (values.Count == 0 || !double.IsFinite(window) || window <= 0) return 0;

        var near = values.Count(v => Math.Abs(v - value) <= window);
        return near / (double)values.Count;
    }

    /// <summary>Share of the baseline that must sit near a reading before it counts as somewhere this entity already goes.</summary>
    public const double KnownModeShare = 0.05;

    /// <summary>And the fewest neighbours that share can be made of, so a short baseline cannot suppress on one stray row.</summary>
    public const int KnownModeSamples = 3;

    public static bool IsKnownMode(IReadOnlyList<double> values, double value, double window)
    {
        if (values.Count == 0) return false;

        var near = values.Count(v => Math.Abs(v - value) <= window);
        return near >= KnownModeSamples && near / (double)values.Count >= KnownModeShare;
    }

    /// <summary>
    /// Entities whose readings are about the plumbing rather than about the house.
    ///
    /// Radio strength, link quality and battery level are all real measurements that vary constantly and mean
    /// nothing on their own — a Zigbee sensor reporting 255 lqi where it usually reports 156 has a better
    /// route to the coordinator, and there is no automation anybody wants out of that.
    ///
    /// Home Assistant already knows this and says so, as <c>entity_category</c>, so that is asked first. But
    /// it lives in the entity registry rather than in the state API and so is not always there — an older
    /// Home Assistant, a proxy that will not pass WebSockets, an integration that never set it — and the
    /// device class, the unit and the naming every integration converges on still answer when it is not.
    ///
    /// Returns why, not just whether, so a decision to stay quiet can be explained rather than just observed.
    /// </summary>
    public static string? DiagnosticReason(HaEntity entity)
    {
        // The user hid it in Home Assistant. Nothing further needs establishing.
        if (entity.Hidden) return "it is hidden in Home Assistant";

        if (entity.IsConfigOrDiagnostic)
            return $"Home Assistant classes it as {entity.EntityCategory!.ToLowerInvariant()}";

        if (InertDomains.Contains(entity.Domain)) return $"the {entity.Domain} domain carries no state worth judging";

        if (entity.DeviceClass is { } deviceClass && DiagnosticClasses.Contains(deviceClass))
            return $"{deviceClass} is a diagnostic reading";

        if (entity.Unit is { } unit && DiagnosticUnits.Contains(unit.Trim()))
            return $"{unit.Trim()} is a diagnostic unit";

        var id = entity.EntityId;
        foreach (var fragment in DiagnosticNames)
            if (id.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return $"'{fragment}' names a diagnostic reading";

        foreach (var fragment in ConfigNames)
            if (id.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return $"'{fragment}' names a device setting rather than something the house does";

        return null;
    }

    /// <summary>A domain that exists to be pressed, shown or updated, and so has no state that can be wrong.</summary>
    public static bool IsInert(HaEntity entity) => InertDomains.Contains(entity.Domain);

    /// <summary>Domains that exist to be pressed, shown or updated; none of them hold a state that can be wrong.</summary>
    private static readonly HashSet<string> InertDomains = new(StringComparer.Ordinal)
    {
        "button", "input_button", "event", "image", "update", "tts", "stt", "conversation",
        "persistent_notification",
    };

    private static readonly HashSet<string> DiagnosticClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "signal_strength", "battery",
    };

    private static readonly HashSet<string> DiagnosticUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        "dBm", "dB", "lqi", "LQI", "dBi",
    };

    private static readonly string[] DiagnosticNames =
    [
        "linkquality", "link_quality", "_rssi", "signal_strength", "bluetooth_signal", "wifi_signal",
        "_uptime", "last_seen", "last_restart", "restart_reason", "update_available", "_firmware",
    ];

    /// <summary>
    /// Names integrations give to the switches and numbers that configure a device.
    ///
    /// These are settings, not behaviour. A real house reported two of them — "Fridge Plug Auto-off enabled
    /// has been off for 50 minutes" — which is not a fridge doing anything, it is a checkbox that has never
    /// been ticked. Matched on the entity id because the registry's <c>entity_category: config</c> is not in
    /// the state API either.
    /// </summary>
    private static readonly string[] ConfigNames =
    [
        "auto_off", "auto_update", "led_indicator", "indicator_light", "child_lock", "power_on_behavior",
        "do_not_disturb", "_calibration", "_sensitivity", "_buzzer", "_beep", "_osd",
    ];

    /// <summary>
    /// The smallest move worth a sentence, in whatever unit the entity reports.
    ///
    /// A z-score says how tight a baseline is, not whether anything happened. On a sensor that has sat very
    /// still, a move far too small to care about scores enormously: a real house was told its disk had gone
    /// from 157.3 to 158.5 GiB and its presence sensor from 3019.5 to 3009 mV, both "well outside their usual
    /// range", and neither is a fact anyone can act on. So a finding has to clear two bars — a share of the
    /// value itself, and a floor in real units for the device classes where one is meaningful.
    /// </summary>
    public static double MinimumMove(HaEntity entity, double median, double current, double fraction)
    {
        // Measured against the larger end so a sensor that normally reads zero is still judged against the
        // size of what it is doing now, rather than against a share of nothing.
        var scale = Math.Max(Math.Abs(median), Math.Abs(current));
        var relative = scale * Math.Max(0, fraction);

        return Math.Max(relative, AbsoluteFloor(entity));
    }

    /// <summary>The per-device-class floor, converted into the unit this entity actually reports in.</summary>
    public static double AbsoluteFloor(HaEntity entity)
    {
        if (entity.DeviceClass is not { } deviceClass) return 0;
        if (!Floors.TryGetValue(deviceClass, out var floor)) return 0;

        var unit = entity.Unit?.Trim();
        if (string.IsNullOrEmpty(unit)) return floor;

        // Temperature is the one difference that is not a plain rescale: a degree Fahrenheit is five ninths
        // of a degree Celsius, with the offset cancelling because this is a gap rather than a reading.
        if (deviceClass.Equals("temperature", StringComparison.OrdinalIgnoreCase))
            return unit.EndsWith('F') ? floor * 1.8 : floor;

        return floor / Multiplier(unit);
    }

    /// <summary>How many base units one of <paramref name="unit"/> is, read off its metric prefix.</summary>
    public static double Multiplier(string unit) => unit.Length < 2
        ? 1
        : unit[0] switch
        {
            'n' => 1e-9,
            'u' or 'µ' => 1e-6,
            'm' => 1e-3,
            'k' or 'K' => 1e3,
            'M' => 1e6,
            'G' => 1e9,
            _ => 1,
        };

    /// <summary>Floors in the device class's base unit: watts, amps, volts, degrees Celsius, percent, and so on.</summary>
    private static readonly Dictionary<string, double> Floors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["power"] = 5,
        ["reactive_power"] = 5,
        ["apparent_power"] = 5,
        ["current"] = 0.5,
        ["voltage"] = 2,
        ["temperature"] = 2.0,
        ["humidity"] = 8,
        ["moisture"] = 8,
        ["pressure"] = 2,
        ["atmospheric_pressure"] = 2,
        ["carbon_dioxide"] = 200,
        ["carbon_monoxide"] = 5,
        ["volatile_organic_compounds"] = 100,
        ["pm1"] = 15,
        ["pm25"] = 15,
        ["pm10"] = 15,
        ["illuminance"] = 50,
        ["sound_pressure"] = 10,
        ["frequency"] = 2,
        ["speed"] = 5,
        ["wind_speed"] = 5,
        ["precipitation_intensity"] = 1,
    };
}
