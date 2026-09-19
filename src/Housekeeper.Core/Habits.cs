using System.Text.Json;

namespace Housekeeper.Core;

/// <summary>
/// Routines found in the stored history: things the user does by hand, regularly enough that an automation
/// could do them instead.
///
/// The detectors say when the house is doing something unusual. This is the other half of watching a
/// house: noticing what the people in it do the same way every day, and offering to take it over. Two
/// shapes cover most of it. A <em>cue</em>: the pantry light goes on within a minute of the pantry motion
/// sensor, nearly every time it fires after dark. A <em>clock</em>: the porch light goes on at about ten
/// past nine, most evenings. Either one, said in the user's own entities with the numbers behind it, is a
/// concrete suggestion rather than a generic example -- and because the suggested wording is a request the
/// drafter already understands, turning it into a real automation is the same one-click path a finding has.
///
/// Everything here is pattern matching over transitions, deterministic and testable. The bar is deliberately
/// high on three axes at once -- how many times, over how many days, and how reliably -- because a routine
/// offered on thin evidence is exactly the kind of clever that people turn off. Several more rules came
/// from running it over a real house and from review. A cue in another room, or in no known room, has to
/// clear a higher bar, because "the kids' room motion sensor fires and the pantry light goes off" is
/// somebody walking through the house, not a routine. Only the strongest cue is offered for any one thing,
/// because four sensors that all see the same person leave the same room are four views of one routine. A
/// light that is also a switch is one thing, not two. A clock routine has to beat what random switching
/// would produce, because the busiest hour of a light used ten times a day at random still holds an event
/// on most days. And something that happens with machine punctuality -- the same second every time -- is
/// an automation nobody told Housekeeper about, not a person. Whatever an automation already does is left
/// out, since the point is what the user is still doing themselves.
/// </summary>
public static class Habits
{
    /// <summary>The fewest different days a routine has to have been seen on. Three is the least that tells a habit from a weekend.</summary>
    public const int MinimumDays = 3;

    /// <summary>How soon after its cue an action has to follow to count as a response to it.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Two entities that change within this of each other, nearly every time, are the same thing seen twice
    /// -- a switched light is very often both a <c>light.</c> and a <c>switch.</c> -- not a cue and a response.
    /// </summary>
    private static readonly TimeSpan Mirror = TimeSpan.FromSeconds(2);

    /// <summary>
    /// A response this soon after its cue, this consistently, is a machine's: a person does not reach a
    /// switch within five seconds every single time, but an automation Housekeeper cannot see does.
    /// </summary>
    private static readonly TimeSpan MachineLag = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan MachineJitter = TimeSpan.FromSeconds(1);

    /// <summary>A clock routine tighter than this over five or more days is a timer, not a person.</summary>
    private static readonly TimeSpan MachineSpread = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The bar for a cue that is not where the effect is: twice the occasions, more days, and at least this
    /// reliable. A person arriving or leaving is exempt, since a person is wherever they are.
    /// </summary>
    public const int FarMinimumDays = 5;

    public const double FarConfidence = 0.8;

    /// <summary>The share of the days on record a routine has to have been seen on, so three days out of a month is not one.</summary>
    public const double DaysShare = 0.2;

    /// <summary>How wide a "same time every day" is allowed to be.</summary>
    public static readonly TimeSpan ClockWidth = TimeSpan.FromMinutes(60);

    /// <summary>The share of days a clock routine has to have happened on.</summary>
    public const double ClockShare = 0.6;

    /// <summary>
    /// An entity changing more often than this, on average, is not being operated by hand. A relay that
    /// cycles every minute, a media player scrubbing through states, a presence-controlled light already on
    /// an automation nobody told Housekeeper about.
    /// </summary>
    public const int MostTransitionsPerDay = 60;

    /// <summary>The most routines offered at once. The scanner applies it, with what the user has already put away in hand.</summary>
    public const int MostOffered = 30;

    /// <summary>The sun entity, whose stored state says whether a cue happened after dark.</summary>
    public const string Sun = "sun.sun";

    /// <summary>Trigger kinds that fire on something happening in the house, as opposed to on the clock.</summary>
    private static readonly HashSet<string> CueTriggers = new(StringComparer.Ordinal)
    {
        "state", "device", "numeric_state", "zone", "event", "template",
    };

    private static readonly HashSet<string> ClockTriggers = new(StringComparer.Ordinal)
    {
        "time", "time_pattern", "sun",
    };

    /// <summary>Domains a person operates: the things a routine ends with.</summary>
    private static readonly HashSet<string> EffectDomains = new(StringComparer.Ordinal)
    {
        "light", "switch", "fan", "humidifier", "cover", "lock", "media_player", "vacuum",
    };

    /// <summary>Domains that only ever report: the things a routine starts from. Effects can be cues too.</summary>
    private static readonly HashSet<string> CueDomains = new(StringComparer.Ordinal)
    {
        "binary_sensor", "person", "device_tracker",
    };

    /// <summary>Binary sensors that report on the device rather than on the house.</summary>
    private static readonly HashSet<string> NotCues = new(StringComparer.OrdinalIgnoreCase)
    {
        "battery", "battery_charging", "connectivity", "update", "problem", "tamper", "running",
    };

    /// <param name="Found">Every routine that held up, strongest first, as findings of kind <see cref="AnomalyKind.Habit"/>. The scanner decides how many to show.</param>
    /// <param name="Automated">Dedup keys of routines that held up but an existing automation already performs.</param>
    /// <param name="MachineMade">Dedup keys of routines too punctual to be a person's: an automation Housekeeper cannot see.</param>
    public sealed record Report(IReadOnlyList<Anomaly> Found, IReadOnlySet<string> Automated, IReadOnlySet<string> MachineMade)
    {
        public static readonly Report Empty = new([], new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
    }

    /// <summary>The entities worth reading history for: anything a routine could start from or end with, and the sun.</summary>
    public static IReadOnlyList<string> Candidates(IReadOnlyList<HaEntity> entities) =>
        [.. entities.Where(IsRoutineMaterial).Select(entity => entity.EntityId)];

    /// <summary>Whether this entity's history could hold a routine at all, as effect or cue. What the watch list's cap should keep.</summary>
    public static bool IsRoutineMaterial(HaEntity entity) => entity.EntityId == Sun || IsEffect(entity) || IsCue(entity);

    internal static bool IsEffect(HaEntity entity) => EffectDomains.Contains(entity.Domain) && Usable(entity);

    internal static bool IsCue(HaEntity entity)
    {
        if (!Usable(entity)) return false;
        if (EffectDomains.Contains(entity.Domain)) return true;
        if (!CueDomains.Contains(entity.Domain)) return false;

        return entity.Domain != "binary_sensor" || entity.DeviceClass is null || !NotCues.Contains(entity.DeviceClass);
    }

    private static bool Usable(HaEntity entity) =>
        !entity.Hidden && !entity.IsConfigOrDiagnostic && !EntityIndex.RarelyAutomated(entity) && Baselines.DiagnosticReason(entity) is null;

    /// <summary>
    /// Finds the routines in <paramref name="samples"/>, which holds weeks of transitions for the entities
    /// <see cref="Candidates"/> named, oldest first per entity.
    /// </summary>
    public static Report Find(
        IReadOnlyList<HaEntity> entities,
        IReadOnlyDictionary<string, IReadOnlyList<StateSample>> samples,
        IReadOnlyList<ExistingAutomation> automations,
        TimeZoneInfo zone,
        ScanOptions options,
        DateTimeOffset nowUtc)
    {
        var house = new House(entities, samples, zone, nowUtc);
        if (house.Effects.Count == 0) return Report.Empty;

        List<(Anomaly Habit, double Strength)> found = [];
        HashSet<string> automated = new(StringComparer.Ordinal);
        HashSet<string> machineMade = new(StringComparer.Ordinal);

        foreach (var effect in house.Effects)
        {
            if (effect.Transitions.Count == 0) continue;
            if (effect.Transitions.Count / Math.Max(1.0, house.DaysOf(effect).TotalDays) > MostTransitionsPerDay) continue;

            foreach (var state in effect.Transitions.Select(transition => transition.To).Distinct(StringComparer.Ordinal))
            {
                var events = effect.Transitions.Where(transition => transition.To == state).ToList();
                if (events.Count < options.HabitMinimumTimes) continue;

                // The moments this state was reached that a cue accounts for, so the clock is not also
                // offered for a routine that is really about the motion sensor. An automation doing it, or
                // a machine doing it, accounts for those moments just as well as a person would.
                HashSet<DateTimeOffset> explained = [];

                List<Candidate> candidates = [];
                foreach (var cue in house.Cues)
                {
                    if (cue.Entity.EntityId == effect.Entity.EntityId) continue;
                    if (cue.Entity.DeviceId is not null && cue.Entity.DeviceId == effect.Entity.DeviceId) continue;
                    if (cue.Transitions.Count == 0) continue;

                    foreach (var candidate in Together(house, effect, state, events, cue, options))
                    {
                        foreach (var at in candidate.Explained) explained.Add(at);

                        if (candidate.MachineMade)
                        {
                            machineMade.Add(candidate.Habit.DedupKey);
                            continue;
                        }

                        if (automations.Any(automation =>
                                automation.Entities.Contains(cue.Entity.EntityId) &&
                                automation.Entities.Contains(effect.Entity.EntityId) &&
                                automation.TriggerKinds.Overlaps(CueTriggers)))
                        {
                            automated.Add(candidate.Habit.DedupKey);
                            continue;
                        }

                        candidates.Add(candidate);
                    }
                }

                // One cue per thing. Several sensors see the same person leave the same room, and every one
                // of them correlates with the light going off; the strongest is the one an automation should
                // be built on. Among equals, the cue that is something happening -- movement seen, a door
                // opened, someone home -- over its ending, because a motion sensor that clears ten seconds
                // after it fired is equally "followed" by the light and would otherwise win on immediacy;
                // then the most immediate. The rest are the same routine.
                var best = candidates
                    .OrderByDescending(candidate => candidate.Strength)
                    .ThenByDescending(candidate => IsActive(candidate.CueState))
                    .ThenBy(candidate => candidate.Lag)
                    .ThenBy(candidate => candidate.Habit.DedupKey, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (best is not null) found.Add((best.Habit, best.Strength));

                if (Clock(house, effect, state, events, explained, options) is { } clock)
                {
                    if (clock.MachineMade)
                        machineMade.Add(clock.Habit.DedupKey);
                    else if (automations.Any(automation =>
                            automation.Entities.Contains(effect.Entity.EntityId) &&
                            automation.TriggerKinds.Overlaps(ClockTriggers)))
                        automated.Add(clock.Habit.DedupKey);
                    else
                        found.Add((clock.Habit, clock.Strength));
                }
            }
        }

        var offered = found
            .OrderByDescending(pair => pair.Strength)
            .ThenBy(pair => pair.Habit.DedupKey, StringComparer.Ordinal)
            .Select(pair => pair.Habit)
            .ToList();

        return new Report(offered, automated, machineMade);
    }

    // ---- cue and response ----

    private sealed record Candidate(Anomaly Habit, double Strength, IReadOnlyList<DateTimeOffset> Explained, TimeSpan Lag, bool MachineMade, string CueState = "");

    /// <summary>A cue state that is something starting rather than ending: movement seen, a door opened, someone home.</summary>
    private static bool IsActive(string state) => state is "on" or "open" or "home" or "playing" or "cleaning" or "unlocked";

    /// <summary>Whether a cue is where the effect is. A person is wherever they are, so arriving and leaving are near to everything.</summary>
    private static bool SameArea(HaEntity cue, HaEntity effect) =>
        cue.Domain is "person" or "device_tracker" ||
        (!string.IsNullOrWhiteSpace(cue.Area) && string.Equals(cue.Area, effect.Area, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Routines of the form "when the cue does this, the effect gets that", under whichever condition makes
    /// them reliable. At most one per cue state, the best.
    /// </summary>
    private static IEnumerable<Candidate> Together(
        House house,
        Track effect,
        string state,
        List<Transition> events,
        Track cue,
        ScanOptions options)
    {
        // Every cue transition inside the window before each response, the newest of each cue state. Only
        // the very last one used to count, which learned a motion sensor that clears in ten seconds as
        // "turn on the light when the sensor STOPS seeing movement" -- the automation nobody wants.
        Dictionary<string, List<(Transition Response, Transition Cue)>> pairs = new(StringComparer.Ordinal);
        foreach (var response in events)
        {
            HashSet<string> seen = [];
            for (var index = cue.LastTransitionAtOrBefore(response.At); index >= 0; index--)
            {
                var before = cue.Transitions[index];
                var lag = response.At - before.At;
                if (lag > Window) break;
                if (!seen.Add(before.To)) continue;

                if (!pairs.TryGetValue(before.To, out var list)) pairs[before.To] = list = [];
                list.Add((response, before));
            }
        }

        // What it takes depends on where the cue is. In the same room, the configured bar; anywhere else,
        // or nowhere known, twice the occasions, more days and a higher share -- and either way, seen on a
        // real share of the days on record rather than on three of thirty.
        var near = SameArea(cue.Entity, effect.Entity);
        var observed = house.DaysOf(effect).TotalDays;
        var fewestTimes = near ? options.HabitMinimumTimes : options.HabitMinimumTimes * 2;
        var fewestDays = Math.Max(near ? MinimumDays : FarMinimumDays, (int)Math.Ceiling(DaysShare * observed));
        var leastShare = near ? options.HabitConfidence : Math.Max(options.HabitConfidence, FarConfidence);

        var responseTimes = events.Select(response => response.At.ToUnixTimeMilliseconds()).ToArray();
        var conditions = house.Conditions;

        foreach (var (cueState, matched) in pairs)
        {
            if (matched.Count < fewestTimes) continue;

            // Two entities that move as one are one thing.
            var lags = matched.Select(pair => (pair.Response.At - pair.Cue.At).TotalSeconds).OrderBy(seconds => seconds).ToArray();
            var typicalLag = TimeSpan.FromSeconds(AnomalyDetection.Median(lags));
            if (typicalLag < Mirror) continue;

            // One pass over the cue's events, tallied under every condition at once. This used to be one
            // pass per condition with a time zone conversion inside each, which on a mid-sized house took
            // minutes inside a scan.
            var occasions = new int[conditions.Count];
            var followed = new int[conditions.Count];
            var days = new HashSet<DateOnly>[conditions.Count];
            var explained = new List<DateTimeOffset>[conditions.Count];
            for (var c = 0; c < conditions.Count; c++)
            {
                days[c] = [];
                explained[c] = [];
            }

            for (var i = 0; i < cue.Transitions.Count; i++)
            {
                var moment = cue.Transitions[i];
                if (moment.To != cueState) continue;

                // Already there, or not known to be anywhere -- before the effect's first sample, or while it
                // was unavailable: the cue asked nothing of the user, so it is neither a hit nor a miss.
                // Otherwise a cue with weeks more history than the effect drowns the routine in misses from
                // before the effect existed.
                var was = effect.StateAt(moment.At);
                if (was is null || string.Equals(was, state, StringComparison.Ordinal)) continue;

                var local = cue.Moments[i];
                var response = FirstWithin(responseTimes, moment.At, Window);

                for (var c = 0; c < conditions.Count; c++)
                {
                    if (!conditions[c].Holds(local)) continue;

                    occasions[c]++;
                    if (response is null) continue;

                    followed[c]++;
                    days[c].Add(local.Date);
                    explained[c].Add(response.Value);
                }
            }

            Tally? best = null;
            for (var c = 0; c < conditions.Count; c++)
            {
                if (followed[c] < fewestTimes || days[c].Count < fewestDays) continue;

                var share = followed[c] / (double)occasions[c];
                if (share < leastShare) continue;

                var tally = new Tally(conditions[c], occasions[c], followed[c], days[c].Count, share, explained[c]);

                // The most reliable condition wins, and among conditions about as reliable the plainer one:
                // "always" over "after dark" over "between four and eight", because the plainer the condition,
                // the simpler the automation it becomes.
                if (best is null || tally.Share > best.Share + 0.1 || (tally.Share >= best.Share - 0.1 && tally.Condition.Rank < best.Condition.Rank && tally.Followed * 2 >= best.Followed))
                    best = tally;
            }

            if (best is null) continue;

            // A response within seconds, to the second, every time: an automation Housekeeper cannot see,
            // most likely one built on a device trigger, which carries no entity id to match against.
            var machineMade = typicalLag < MachineLag && best.Share >= 0.9 && Jitter(lags) <= MachineJitter;

            var first = matched.Min(pair => pair.Response.At);
            var last = matched.Max(pair => pair.Response.At);
            var habit = TogetherHabit(house, effect, state, cue, cueState, best, matched.Count, events.Count, typicalLag, first, last);

            yield return new Candidate(habit, best.Followed * best.Share, best.Explained, typicalLag, machineMade, cueState);
        }
    }

    private sealed record Tally(Condition Condition, int Occasions, int Followed, int Days, double Share, List<DateTimeOffset> Explained);

    /// <summary>How much the middle half of the lags vary: the inter-quartile range.</summary>
    private static TimeSpan Jitter(double[] sortedLags)
    {
        if (sortedLags.Length < 4) return TimeSpan.MaxValue;
        var lower = AnomalyDetection.Quantile(sortedLags, 0.25);
        var upper = AnomalyDetection.Quantile(sortedLags, 0.75);
        return TimeSpan.FromSeconds(upper - lower);
    }

    /// <summary>The first response after <paramref name="after"/> and within <paramref name="window"/> of it, or null.</summary>
    private static DateTimeOffset? FirstWithin(long[] sortedTimes, DateTimeOffset after, TimeSpan window)
    {
        var from = after.ToUnixTimeMilliseconds();
        var index = Array.BinarySearch(sortedTimes, from + 1);
        if (index < 0) index = ~index;
        if (index >= sortedTimes.Length) return null;

        var at = sortedTimes[index];
        return at - from <= (long)window.TotalMilliseconds ? DateTimeOffset.FromUnixTimeMilliseconds(at) : null;
    }

    private static Anomaly TogetherHabit(
        House house,
        Track effect,
        string state,
        Track cue,
        string cueState,
        Tally best,
        int matched,
        int responses,
        TimeSpan lag,
        DateTimeOffset first,
        DateTimeOffset last)
    {
        var y = effect.Entity;
        var x = cue.Entity;
        var condition = best.Condition;
        var percent = (int)Math.Round(best.Share * 100);

        var what = $"You usually {Verb(y, state, lower: true)} the {Name(y)} when {Subject(x)} {CueWords(x, cueState)}{condition.Spoken}.";
        var why = $"Seen {best.Followed} times over {best.Days} days: when {Subject(x)} {CueWords(x, cueState)}{condition.Spoken} " +
                  $"while the {Name(y)} is {Opposite(y, state)}, you {VerbIt(y, state)} within about {Ha.Duration(lag)} {percent}% of the time. " +
                  $"That is {matched} of the {responses} times you {VerbIt(y, state)} at all.";

        var request = $"{Verb(y, state, lower: false)} {y.EntityId} when {x.EntityId} {CueWords(x, cueState)}{condition.Clause}.";
        var spoken = $"{Verb(y, state, lower: false)} the {Name(y)} when {Subject(x)} {CueWords(x, cueState)}{condition.Clause}.";

        return new Anomaly
        {
            DedupKey = $"habit:{y.EntityId}:{state}:{x.EntityId}:{cueState}",
            EntityId = y.EntityId,
            Kind = AnomalyKind.Habit,
            DetectedUtc = house.NowUtc,
            Severity = Math.Round(1 + best.Share, 2),
            Summary = what + " " + why,
            SuggestedRequest = request,
            EvidenceJson = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["habit"] = "cue",
                ["entity_name"] = y.FriendlyName,
                ["device"] = y.DeviceName,
                ["area"] = y.Area,
                ["effect"] = y.EntityId,
                ["effect_state"] = state,
                ["cue"] = x.EntityId,
                ["cue_name"] = x.FriendlyName,
                ["cue_state"] = cueState,
                ["condition"] = condition.Key,
                ["times"] = best.Followed,
                ["out_of"] = best.Occasions,
                ["days"] = best.Days,
                ["share"] = Math.Round(best.Share, 3),
                ["explains"] = Math.Round(matched / (double)responses, 3),
                ["explains_times"] = matched,
                ["effect_times"] = responses,
                ["lag_seconds"] = Math.Round(lag.TotalSeconds),
                ["first_seen"] = first,
                ["last_seen"] = last,
                ["what"] = what,
                ["why"] = why,
                ["spoken"] = spoken,
            }),
        };
    }

    // ---- the clock ----

    /// <summary>A routine of the form "at about this time, most days", unless a cue already explains it.</summary>
    private static Candidate? Clock(
        House house,
        Track effect,
        string state,
        List<Transition> events,
        HashSet<DateTimeOffset> explained,
        ScanOptions options)
    {
        var moments = new List<(DateTimeOffset At, LocalMoment Local)>(events.Count);
        for (var i = 0; i < effect.Transitions.Count; i++)
            if (effect.Transitions[i].To == state)
                moments.Add((effect.Transitions[i].At, effect.Moments[i]));

        var width = (int)ClockWidth.TotalMinutes;

        // The densest hour of the day, counted in distinct days rather than events, so one busy evening does
        // not make a habit. Circular, so a window can straddle midnight.
        List<(DateTimeOffset At, LocalMoment Local)> best = [];
        var bestDays = 0;
        foreach (var start in moments.Select(pair => pair.Local.MinuteOfDay).Distinct().OrderBy(minute => minute))
        {
            var inside = moments.Where(pair => ((pair.Local.MinuteOfDay - start) % 1440 + 1440) % 1440 < width).ToList();
            var days = inside.Select(pair => pair.Local.Date).Distinct().Count();

            if (days > bestDays)
            {
                bestDays = days;
                best = inside;
            }
        }

        if (bestDays < MinimumDays || best.Count < options.HabitMinimumTimes) return null;

        var observed = house.DaysOf(effect);
        var (weekdays, weekends) = house.SplitDays(effect);

        // What random switching would produce. A light used k times a day at random still has an event in
        // its busiest hour on many days, and the busiest hour is chosen after the fact, so a routine has to
        // beat that by a clear margin. The null model spreads the day's events evenly over the span of the
        // day this entity is actually used in, never narrower than a few hours.
        var perDay = events.Count / Math.Max(1.0, observed.TotalDays);
        var active = Math.Max(4 * width, ActiveSpan(moments.Select(pair => pair.Local.MinuteOfDay).ToList()));
        var chance = 1 - Math.Pow(1 - width / (double)active, perDay);

        var dates = best.Select(pair => pair.Local.Date).Distinct().ToList();
        var onWeekdays = dates.Count(date => !House.IsWeekend(date));
        var onWeekends = dates.Count - onWeekdays;

        var weekdayShare = weekdays > 0 ? onWeekdays / (double)weekdays : 0;
        var weekendShare = weekends > 0 ? onWeekends / (double)weekends : 0;

        // Every day only if both halves of the week carry it: fifteen weekday mornings out of twenty-one
        // days clears the overall bar and is still a weekday routine, and an automation that fired on
        // Saturday would be wrong six times in seven weeks.
        string? part;
        int covered, outOf;
        if (dates.Count >= ClockShare * Math.Max(1, observed.TotalDays) &&
            (weekends < 2 || weekendShare >= ClockShare) &&
            (weekdays < MinimumDays || weekdayShare >= ClockShare))
        {
            part = null;
            covered = dates.Count;
            outOf = (int)Math.Ceiling(observed.TotalDays);
        }
        else if (weekdays >= MinimumDays && onWeekdays >= MinimumDays && weekdayShare >= ClockShare)
        {
            part = "weekdays";
            covered = onWeekdays;
            outOf = weekdays;
            best = [.. best.Where(pair => !pair.Local.Weekend)];
        }
        else if (weekends >= MinimumDays && onWeekends >= MinimumDays && weekendShare >= ClockShare)
        {
            part = "weekends";
            covered = onWeekends;
            outOf = weekends;
            best = [.. best.Where(pair => pair.Local.Weekend)];
        }
        else
        {
            return null;
        }

        // The bar applies to the part actually offered: three weekday mornings out of five events is not a
        // weekday routine, and a cue that explains the weekday half explains it.
        if (best.Count < options.HabitMinimumTimes) return null;
        if (best.Count(pair => explained.Contains(pair.At)) * 2 >= best.Count) return null;
        if (!BeatsChance(covered, outOf, chance)) return null;

        var minutes = best.Select(pair => pair.Local.MinuteOfDay).ToList();
        var centre = CircularMean(minutes);
        var earliest = minutes.Min(minute => ((minute - centre) % 1440 + 2160) % 1440 - 720);
        var latest = minutes.Max(minute => ((minute - centre) % 1440 + 2160) % 1440 - 720);

        // Within two minutes on five or more days is a timer, not a person.
        var machineMade = covered >= FarMinimumDays && latest - earliest <= MachineSpread.TotalMinutes;

        var y = effect.Entity;
        var at = Clock(centre);
        var span = $"between {Clock(centre + earliest)} and {Clock(centre + latest)}";
        var when = part is null ? "" : $" on {part}";
        var dayWord = part switch { "weekdays" => "weekdays", "weekends" => "weekend days", _ => "days" };

        var what = $"You usually {Verb(y, state, lower: true)} the {Name(y)} at about {at}{when}.";
        var why = $"On {covered} of the last {outOf} {dayWord}, {span}.";
        var request = $"{Verb(y, state, lower: false)} {y.EntityId} at {at}{when}.";
        var spoken = $"{Verb(y, state, lower: false)} the {Name(y)} at {at}{when}.";

        var habit = new Anomaly
        {
            DedupKey = $"habit:{y.EntityId}:{state}:clock",
            EntityId = y.EntityId,
            Kind = AnomalyKind.Habit,
            DetectedUtc = house.NowUtc,
            Severity = Math.Round(1 + covered / (double)outOf, 2),
            Summary = what + " " + why,
            SuggestedRequest = request,
            EvidenceJson = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["habit"] = "clock",
                ["entity_name"] = y.FriendlyName,
                ["device"] = y.DeviceName,
                ["area"] = y.Area,
                ["effect"] = y.EntityId,
                ["effect_state"] = state,
                ["time"] = at,
                ["earliest"] = Clock(centre + earliest),
                ["latest"] = Clock(centre + latest),
                ["part"] = part ?? "daily",
                ["times"] = best.Count,
                ["days"] = covered,
                ["out_of"] = outOf,
                ["share"] = Math.Round(covered / (double)outOf, 3),
                ["chance"] = Math.Round(chance, 3),
                ["first_seen"] = best.Min(pair => pair.At),
                ["last_seen"] = best.Max(pair => pair.At),
                ["what"] = what,
                ["why"] = why,
                ["spoken"] = spoken,
            }),
        };

        return new Candidate(habit, covered / (double)outOf * best.Count, [], TimeSpan.Zero, machineMade);
    }

    /// <summary>
    /// Whether <paramref name="hit"/> days out of <paramref name="days"/> is more than random switching would
    /// give a window chosen after the fact, where <paramref name="chance"/> is the probability that a random
    /// day lands an event in a fixed window. The busiest hour is the best of hundreds of overlapping windows,
    /// so the bar is four standard deviations clear of the expectation and twice the expectation, whichever
    /// is stricter; a small absolute margin covers the days too few for the normal approximation. A light
    /// used twenty times a day at random clears neither; a routine on most days clears both.
    /// </summary>
    internal static bool BeatsChance(int hit, int days, double chance)
    {
        if (days <= 0) return false;
        var expected = days * chance;
        var deviation = Math.Sqrt(days * chance * (1 - chance));
        return hit > expected + Math.Max(3, 4 * deviation) && hit >= 2 * expected;
    }

    /// <summary>The shortest arc of the day that holds every minute given: how much of the day this thing is used in.</summary>
    internal static int ActiveSpan(IReadOnlyList<int> minutes)
    {
        if (minutes.Count == 0) return 0;

        var sorted = minutes.Distinct().Order().ToList();
        if (sorted.Count == 1) return 1;

        var largestGap = 0;
        for (var i = 1; i < sorted.Count; i++) largestGap = Math.Max(largestGap, sorted[i] - sorted[i - 1]);
        largestGap = Math.Max(largestGap, sorted[0] + 1440 - sorted[^1]);

        return 1440 - largestGap;
    }

    /// <summary>The mean of minutes-of-day taken around the clock, so 23:50 and 00:10 average to midnight, not to noon.</summary>
    internal static int CircularMean(IReadOnlyList<int> minutes)
    {
        if (minutes.Count == 0) return 0;

        double x = 0, y = 0;
        foreach (var minute in minutes)
        {
            var angle = minute / 1440.0 * 2 * Math.PI;
            x += Math.Cos(angle);
            y += Math.Sin(angle);
        }

        var mean = Math.Atan2(y, x) / (2 * Math.PI) * 1440;
        return ((int)Math.Round(mean) % 1440 + 1440) % 1440;
    }

    private static string Clock(int minuteOfDay)
    {
        var minute = (minuteOfDay % 1440 + 1440) % 1440;
        return $"{minute / 60:00}:{minute % 60:00}";
    }

    // ---- words ----

    /// <summary>The friendly name, or the id said as words. Never the id itself: the point is to read naturally.</summary>
    private static string Name(HaEntity entity)
    {
        if (!string.IsNullOrWhiteSpace(entity.FriendlyName)) return entity.FriendlyName.Trim();

        var dot = entity.EntityId.IndexOf('.');
        return (dot > 0 ? entity.EntityId[(dot + 1)..] : entity.EntityId).Replace('_', ' ');
    }

    /// <summary>The cue as it reads in a sentence: "the Pantry motion sensor", but "Sam" and "Sam's phone".</summary>
    private static string Subject(HaEntity entity) =>
        entity.Domain is "person" or "device_tracker" ? Name(entity) : "the " + Name(entity);

    private static string Verb(HaEntity entity, string state, bool lower)
    {
        var verb = (entity.Domain, state) switch
        {
            ("cover", "open") => "Open",
            ("cover", "closed") => "Close",
            ("lock", "locked") => "Lock",
            ("lock", "unlocked") => "Unlock",
            ("vacuum", "cleaning") => "Start",
            ("vacuum", "docked") => "Send home",
            (_, "on") or (_, "playing") => "Turn on",
            _ => "Turn off",
        };

        return lower ? char.ToLowerInvariant(verb[0]) + verb[1..] : verb;
    }

    /// <summary>The verb with its object in the middle: "turn it on", "send it home".</summary>
    private static string VerbIt(HaEntity entity, string state) => (entity.Domain, state) switch
    {
        ("cover", "open") => "open it",
        ("cover", "closed") => "close it",
        ("lock", "locked") => "lock it",
        ("lock", "unlocked") => "unlock it",
        ("vacuum", "cleaning") => "start it",
        ("vacuum", "docked") => "send it home",
        (_, "on") or (_, "playing") => "turn it on",
        _ => "turn it off",
    };

    /// <summary>The state a thing is in before the routine moves it: off before on, closed before open.</summary>
    private static string Opposite(HaEntity entity, string state) => (entity.Domain, state) switch
    {
        ("cover", "open") => "closed",
        ("cover", "closed") => "open",
        ("lock", "locked") => "unlocked",
        ("lock", "unlocked") => "locked",
        ("media_player", "playing") => "off",
        ("media_player", _) => "playing",
        ("vacuum", "cleaning") => "docked",
        ("vacuum", "docked") => "out",
        (_, "on") => "off",
        _ => "on",
    };

    /// <summary>What the cue did, as a verb phrase: "detects movement", "opens", "arrives home".</summary>
    internal static string CueWords(HaEntity entity, string state)
    {
        var deviceClass = entity.DeviceClass?.Trim().ToLowerInvariant();

        return (entity.Domain, deviceClass, state) switch
        {
            ("binary_sensor", "motion", "on") => "detects movement",
            ("binary_sensor", "motion", "off") => "stops detecting movement",
            ("binary_sensor", "occupancy" or "presence", "on") => "detects someone",
            ("binary_sensor", "occupancy" or "presence", "off") => "no longer detects anyone",
            ("binary_sensor", "door" or "window" or "opening" or "garage_door" or "garage", "on") => "opens",
            ("binary_sensor", "door" or "window" or "opening" or "garage_door" or "garage", "off") => "closes",
            ("binary_sensor", "moisture", "on") => "detects water",
            ("binary_sensor", "moisture", "off") => "is dry again",
            ("binary_sensor", "light", "on") => "sees light",
            ("binary_sensor", "light", "off") => "sees dark",
            ("binary_sensor", "sound", "on") => "hears something",
            ("binary_sensor", "vibration", "on") => "detects vibration",
            ("binary_sensor", _, "on") => "turns on",
            ("binary_sensor", _, "off") => "turns off",
            ("person" or "device_tracker", _, "home") => "arrives home",
            ("person" or "device_tracker", _, _) => "leaves home",
            ("cover", _, "open") => "opens",
            ("cover", _, "closed") => "closes",
            ("lock", _, "locked") => "is locked",
            ("lock", _, "unlocked") => "is unlocked",
            ("media_player", _, "playing") => "starts playing",
            ("media_player", _, _) => "is turned off",
            ("vacuum", _, "cleaning") => "starts cleaning",
            ("vacuum", _, "docked") => "returns to its dock",
            (_, _, "on") => "is turned on",
            _ => "is turned off",
        };
    }

    /// <summary>
    /// One state name per thing that matters, so "opening" and "open" are one transition and a media player
    /// pausing is not a change of anything a routine is about. Null for a state that says nothing here.
    /// </summary>
    internal static string? Canon(string domain, string state)
    {
        var s = state.Trim().ToLowerInvariant();
        return domain switch
        {
            "light" or "switch" or "fan" or "humidifier" or "binary_sensor" => s is "on" or "off" ? s : null,
            "cover" => s switch { "open" or "opening" => "open", "closed" or "closing" => "closed", _ => null },
            "lock" => s switch { "locked" or "locking" => "locked", "unlocked" or "unlocking" => "unlocked", _ => null },
            "media_player" => s switch { "playing" => "playing", "off" => "off", _ => null },
            "vacuum" => s switch { "cleaning" => "cleaning", "docked" => "docked", _ => null },
            "person" or "device_tracker" => s == "home" ? "home" : "away",
            "sun" => s,
            _ => null,
        };
    }

    // ---- the house, as seen from its history ----

    internal sealed record Transition(DateTimeOffset At, string From, string To);

    /// <summary>A moment in the house's own time: worked out once per transition, read by every condition.</summary>
    /// <param name="Dark">Whether the sun was below the horizon; null when no sun history says.</param>
    internal readonly record struct LocalMoment(DateOnly Date, int Hour, int MinuteOfDay, bool Weekend, bool? Dark);

    /// <summary>One entity's history: its transitions, the state it was in at any moment, and each transition's local time.</summary>
    internal sealed class Track
    {
        public HaEntity Entity { get; }
        public IReadOnlyList<Transition> Transitions { get; }
        public DateTimeOffset? First { get; }

        /// <summary>The local time of each transition, parallel to <see cref="Transitions"/>. Set once by the house.</summary>
        public LocalMoment[] Moments { get; private set; } = [];

        private readonly long[] _times;
        private readonly string?[] _states;
        private readonly long[] _transitionTimes;

        public Track(HaEntity entity, IReadOnlyList<StateSample> samples)
        {
            Entity = entity;

            var ordered = samples.OrderBy(sample => sample.ChangedUtc).ToList();
            _times = [.. ordered.Select(sample => sample.ChangedUtc.ToUnixTimeMilliseconds())];
            _states = [.. ordered.Select(sample => Ha.IsUnavailable(sample.State) ? null : Canon(entity.Domain, sample.State))];
            First = ordered.Count == 0 ? null : ordered[0].ChangedUtc;

            List<Transition> transitions = [];
            string? previous = null;
            var known = false;
            foreach (var sample in ordered)
            {
                // Coming back from unavailable is the device reconnecting, not a person doing anything.
                if (Ha.IsUnavailable(sample.State))
                {
                    previous = null;
                    known = false;
                    continue;
                }

                var state = Canon(entity.Domain, sample.State);
                if (state is null) continue;

                if (known && !string.Equals(state, previous, StringComparison.Ordinal))
                    transitions.Add(new Transition(sample.ChangedUtc, previous!, state));

                previous = state;
                known = true;
            }

            Transitions = transitions;
            _transitionTimes = [.. transitions.Select(transition => transition.At.ToUnixTimeMilliseconds())];
        }

        public void Localise(TimeZoneInfo zone, Track? sun)
        {
            var moments = new LocalMoment[Transitions.Count];
            for (var i = 0; i < moments.Length; i++)
            {
                var local = TimeZoneInfo.ConvertTime(Transitions[i].At, zone);
                var date = DateOnly.FromDateTime(local.DateTime);
                bool? dark = sun?.StateAt(Transitions[i].At) switch
                {
                    "below_horizon" => true,
                    "above_horizon" => false,
                    _ => null,
                };
                moments[i] = new LocalMoment(date, local.Hour, local.Hour * 60 + local.Minute, House.IsWeekend(date), dark);
            }

            Moments = moments;
        }

        /// <summary>The canonical state in force at a moment, or null when it is not known.</summary>
        public string? StateAt(DateTimeOffset at)
        {
            var index = Array.BinarySearch(_times, at.ToUnixTimeMilliseconds());
            if (index < 0) index = ~index - 1;
            return index < 0 ? null : _states[index];
        }

        /// <summary>The index of the newest transition at or before a moment, or -1.</summary>
        public int LastTransitionAtOrBefore(DateTimeOffset at)
        {
            var index = Array.BinarySearch(_transitionTimes, at.ToUnixTimeMilliseconds());
            return index < 0 ? ~index - 1 : index;
        }

        /// <summary>Whether a transition happened within <paramref name="within"/> either side of a moment.</summary>
        public bool HasTransitionNear(DateTimeOffset at, TimeSpan within)
        {
            var index = LastTransitionAtOrBefore(at + within);
            return index >= 0 && at - Transitions[index].At <= within;
        }

        /// <summary>
        /// True when nearly every transition of the smaller history has one of the other's within a couple
        /// of seconds: two ids for one thing, such as the light and the switch of a switched light.
        /// </summary>
        public bool MovesWith(Track other)
        {
            var (fewer, more) = Transitions.Count <= other.Transitions.Count ? (this, other) : (other, this);
            if (fewer.Transitions.Count < 3) return false;

            var together = fewer.Transitions.Count(transition => more.HasTransitionNear(transition.At, Mirror));
            return together >= 0.8 * fewer.Transitions.Count;
        }
    }

    /// <summary>A condition a routine may hold under, ordered from the plainest to the most particular.</summary>
    /// <param name="Clause">How the drafter is asked for it, in a request.</param>
    /// <param name="Spoken">How the card says it.</param>
    internal sealed record Condition(string Key, int Rank, string Clause, string Spoken, Func<LocalMoment, bool> Holds);

    internal sealed class House
    {
        public IReadOnlyList<Track> Effects { get; }
        public IReadOnlyList<Track> Cues { get; }
        public IReadOnlyList<Condition> Conditions { get; }
        public DateTimeOffset NowUtc { get; }

        private readonly TimeZoneInfo _zone;

        public House(
            IReadOnlyList<HaEntity> entities,
            IReadOnlyDictionary<string, IReadOnlyList<StateSample>> samples,
            TimeZoneInfo zone,
            DateTimeOffset nowUtc)
        {
            _zone = zone;
            NowUtc = nowUtc;

            Dictionary<string, Track> tracks = new(StringComparer.Ordinal);
            foreach (var entity in entities)
            {
                if (!samples.TryGetValue(entity.EntityId, out var history) || history.Count == 0) continue;
                if (IsRoutineMaterial(entity)) tracks[entity.EntityId] = new Track(entity, history);
            }

            // The sun may be recorded without being in the entity list handed over; its history is the
            // condition, not a routine, so it is taken from the samples on its own.
            var sun = tracks.GetValueOrDefault(Sun);
            if (sun is null && samples.TryGetValue(Sun, out var sunHistory) && sunHistory.Count > 0)
                sun = new Track(Build(Sun), sunHistory);

            foreach (var track in tracks.Values) track.Localise(zone, sun);

            // A switched light is very often both a light. and a switch. entity, changing as one. Keeping both
            // would offer every routine twice, and the light is the one a person means.
            HashSet<string> twins = new(StringComparer.Ordinal);
            var effects = tracks.Values
                .Where(track => IsEffect(track.Entity))
                .OrderBy(track => Preference(track.Entity.Domain))
                .ThenBy(track => track.Entity.EntityId, StringComparer.Ordinal)
                .ToList();
            for (var i = 0; i < effects.Count; i++)
            {
                if (twins.Contains(effects[i].Entity.EntityId)) continue;
                for (var j = i + 1; j < effects.Count; j++)
                    if (!twins.Contains(effects[j].Entity.EntityId) && effects[i].MovesWith(effects[j]))
                        twins.Add(effects[j].Entity.EntityId);
            }

            Effects = [.. effects.Where(track => !twins.Contains(track.Entity.EntityId)).OrderBy(track => track.Entity.EntityId, StringComparer.Ordinal)];
            Cues = [.. tracks.Values.Where(track => IsCue(track.Entity) && !twins.Contains(track.Entity.EntityId)).OrderBy(track => track.Entity.EntityId, StringComparer.Ordinal)];

            List<Condition> conditions =
            [
                new("always", 0, "", "", _ => true),
            ];

            // "When it is dark" rather than "after sunset": Home Assistant's after-sunset condition is true
            // only from sunset to midnight, and the routine was counted right through to sunrise.
            if (sun is not null && sun.Transitions.Count > 0)
            {
                conditions.Add(new("dark", 1, " when it is dark", " after dark", moment => moment.Dark == true));
                conditions.Add(new("daylight", 2, " during the day", " during the day", moment => moment.Dark == false));
            }

            conditions.Add(new("weekdays", 3, " on weekdays", " on weekdays", moment => !moment.Weekend));
            conditions.Add(new("weekends", 4, " at weekends", " at weekends", moment => moment.Weekend));

            // The last band ends at midnight, which Home Assistant's time condition writes as 00:00, not
            // 24:00; and a person reads "midnight".
            for (var band = 0; band < 6; band++)
            {
                var from = band * 4;
                var to = (from + 4) % 24;
                var clause = $" between {from:00}:00 and {to:00}:00";
                var spoken = to == 0 ? $" between {from:00}:00 and midnight" : clause;
                var which = band;
                conditions.Add(new($"band{band}", 5 + band, clause, spoken, moment => moment.Hour / 4 == which));
            }

            Conditions = conditions;
        }

        private static HaEntity Build(string entityId) => new(entityId, "unknown", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

        /// <summary>Which of two ids for one thing to keep: the one that says what the thing is.</summary>
        private static int Preference(string domain) => domain switch
        {
            "light" => 0,
            "cover" => 1,
            "lock" => 2,
            "fan" => 3,
            "media_player" => 4,
            "humidifier" => 5,
            "vacuum" => 6,
            _ => 7,
        };

        public DateOnly DateOf(DateTimeOffset at) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, _zone).DateTime);

        public static bool IsWeekend(DateOnly date) => date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

        /// <summary>How long this entity has been on record, in whole local days, never less than one.</summary>
        public TimeSpan DaysOf(Track track)
        {
            if (track.First is not { } first) return TimeSpan.FromDays(1);
            var days = DateOf(NowUtc).DayNumber - DateOf(first).DayNumber + 1;
            return TimeSpan.FromDays(Math.Max(1, days));
        }

        /// <summary>How many of the days on record were weekdays, and how many weekend days.</summary>
        public (int Weekdays, int Weekends) SplitDays(Track track)
        {
            if (track.First is not { } first) return (0, 0);

            int weekdays = 0, weekends = 0;
            for (var date = DateOf(first); date <= DateOf(NowUtc); date = date.AddDays(1))
            {
                if (IsWeekend(date)) weekends++;
                else weekdays++;
            }

            return (weekdays, weekends);
        }
    }
}
