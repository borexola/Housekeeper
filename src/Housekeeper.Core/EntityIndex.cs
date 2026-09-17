namespace Housekeeper.Core;

/// <summary>
/// Chooses which entities are worth showing the model for a given request, and which are worth observing.
/// Deterministic and cheap: a large install has thousands of entities and none of them belong in a prompt.
/// </summary>
public static class EntityIndex
{
    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "any", "are", "as", "at", "be", "been", "but", "by", "do", "does", "for", "from",
        "get", "has", "have", "i", "if", "in", "is", "it", "its", "just", "let", "make", "me", "more", "most",
        "my", "no", "not", "of", "off", "on", "one", "or", "please", "set", "should", "so", "than", "that",
        "the", "then", "this", "to", "turn", "us", "very", "was", "we", "were", "when", "whenever", "will",
        "with", "would",
    };

    /// <summary>Words that imply a domain even when no entity is named, e.g. "lights" implies light + switch.</summary>
    private static readonly Dictionary<string, string[]> DomainHints = new(StringComparer.Ordinal)
    {
        ["light"] = ["light", "switch"],
        ["lamp"] = ["light"],
        ["brightness"] = ["light"],
        ["home"] = ["person", "device_tracker"],
        ["away"] = ["person", "device_tracker"],
        ["presence"] = ["person", "device_tracker", "binary_sensor"],
        ["occupied"] = ["binary_sensor", "person"],
        ["nobody"] = ["person", "device_tracker"],
        ["anyone"] = ["person", "device_tracker"],
        ["someone"] = ["person", "device_tracker"],
        ["motion"] = ["binary_sensor"],
        ["door"] = ["binary_sensor", "cover", "lock"],
        ["window"] = ["binary_sensor", "cover"],
        ["open"] = ["binary_sensor", "cover"],
        ["closed"] = ["binary_sensor", "cover"],
        ["lock"] = ["lock"],
        ["locked"] = ["lock"],
        ["unlocked"] = ["lock"],
        ["temperature"] = ["sensor", "climate"],
        ["temp"] = ["sensor", "climate"],
        ["humidity"] = ["sensor"],
        ["thermostat"] = ["climate"],
        ["heating"] = ["climate"],
        ["cooling"] = ["climate"],
        ["power"] = ["sensor"],
        ["energy"] = ["sensor"],
        ["battery"] = ["sensor"],
        ["plug"] = ["switch"],
        ["outlet"] = ["switch"],
        ["socket"] = ["switch"],
        ["fan"] = ["fan"],
        ["vacuum"] = ["vacuum"],
        ["alarm"] = ["alarm_control_panel"],
        ["camera"] = ["camera"],
        ["blind"] = ["cover"],
        ["curtain"] = ["cover"],
        ["garage"] = ["cover"],
        ["media"] = ["media_player"],
        ["tv"] = ["media_player"],
        ["music"] = ["media_player"],
        ["speaker"] = ["media_player"],
        ["sun"] = ["sun"],
        ["sunset"] = ["sun"],
        ["sunrise"] = ["sun"],
        ["weather"] = ["weather"],
    };

    /// <summary>Splits free text into lowercase, de-pluralised, meaningful tokens.</summary>
    public static IReadOnlyList<string> Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        List<string> tokens = [];
        var start = -1;

        for (var i = 0; i <= text.Length; i++)
        {
            var isWord = i < text.Length && char.IsLetterOrDigit(text[i]);

            if (isWord)
            {
                if (start < 0) start = i;
                continue;
            }

            if (start < 0) continue;

            var word = text[start..i].ToLowerInvariant();
            start = -1;

            if (word.Length >= 2 && !Stopwords.Contains(word))
                tokens.Add(Singular(word));
        }

        return tokens;
    }

    /// <summary>Trims a simple English plural so "lights" and "light" match.</summary>
    public static string Singular(string word) =>
        word.Length > 3 && word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal)
            ? word[..^1]
            : word;

    /// <summary>Matches an entity id against a glob supporting <c>*</c> and <c>?</c>.</summary>
    public static bool GlobMatch(string pattern, string value)
    {
        int p = 0, v = 0, star = -1, mark = 0;

        while (v < value.Length)
        {
            if (p < pattern.Length &&
                (pattern[p] == '?' || char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(value[v])))
            {
                p++;
                v++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = v;
            }
            else if (star >= 0)
            {
                p = star + 1;
                v = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    /// <summary>Applies the observe/ignore globs from <see cref="ScanOptions"/>.</summary>
    public static IReadOnlyList<HaEntity> Filter(IEnumerable<HaEntity> entities, ScanOptions options)
    {
        var included = entities.Where(e =>
            (options.IncludeAll || options.Include.Any(p => GlobMatch(p, e.EntityId))) &&
            !options.Exclude.Any(p => GlobMatch(p, e.EntityId)));

        return [.. included.OrderBy(e => e.EntityId, StringComparer.Ordinal).Take(options.MaxTrackedEntities)];
    }

    /// <summary>
    /// Domains worth offering when the wording matched little or nothing, most commonly automated first.
    /// "Make the hallway cozy at night" shares no token with <c>light.hall</c>; the model still needs to see it.
    /// </summary>
    private static readonly string[] FallbackDomains =
    [
        "light", "switch", "person", "device_tracker", "binary_sensor", "climate", "cover", "lock",
        "media_player", "fan", "sensor", "input_boolean", "scene", "script",
    ];

    /// <summary>Below this many matches the shortlist is padded from <see cref="FallbackDomains"/>.</summary>
    private const int PadFloor = 12;

    /// <summary>
    /// Entities nobody writes an automation about: how well a sensor hears its hub, how long a box has been
    /// up, what firmware it carries, and the calibration knobs an integration exposes because it can.
    ///
    /// This is a different question from the one the detectors ask, and the two must not be run together.
    /// <see cref="Baselines.DiagnosticReason"/> asks whether an unusual READING is worth reporting, and the
    /// answer for a battery is no, because a battery going down is a battery. But "tell me when the battery
    /// is low" is one of the most common automations anyone writes, and Home Assistant files battery level
    /// under <c>entity_category: diagnostic</c> — so deciding this from the category alone would quietly
    /// make that request unanswerable. The same goes for updates and for connectivity.
    ///
    /// What is actually listed here is the narrow set that is never the subject of a request, judged from
    /// the device class, the unit and the naming integrations converge on.
    /// </summary>
    public static bool RarelyAutomated(HaEntity entity)
    {
        if (NeverAutomatedDomains.Contains(entity.Domain)) return true;

        // An update entity exists to be acted on -- "tell me when there is an update" is one of the
        // commonest requests there is. It is exempted before the names are read because the same word means
        // opposite things either side of the dot: sensor.hub_firmware is a version string nobody automates,
        // while update.hub_firmware_update is the thing they are asking about.
        if (entity.Domain == "update") return false;

        if (string.Equals(entity.DeviceClass, "signal_strength", StringComparison.OrdinalIgnoreCase)) return true;

        if (entity.Unit is { } unit && SignalUnits.Contains(unit.Trim())) return true;

        foreach (var fragment in NeverAutomatedNames)
            if (entity.EntityId.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    /// <summary>
    /// Domains that hold no state anyone triggers on and are no use as a target either. Deliberately short:
    /// <c>button</c> is a perfectly ordinary thing to press from an automation, <c>event</c> is how modern
    /// integrations report a button being pressed, and "tell me when there is an update" is a real request,
    /// so none of those belong here however diagnostic they look.
    /// </summary>
    private static readonly HashSet<string> NeverAutomatedDomains = new(StringComparer.Ordinal)
    {
        "image", "stt", "tts", "conversation",
    };

    private static readonly HashSet<string> SignalUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        "dBm", "lqi", "dBi",
    };

    /// <summary>
    /// Names that mean the entity is about the plumbing. Note what is absent: nothing matching battery,
    /// update or connectivity, because all three are ordinary things to automate.
    /// </summary>
    private static readonly string[] NeverAutomatedNames =
    [
        "linkquality", "link_quality", "_rssi", "signal_strength", "bluetooth_signal", "wifi_signal",
        "_uptime", "last_seen", "last_restart", "restart_reason", "_firmware", "_checksum",
        "_calibration", "_sensitivity", "led_indicator", "indicator_light", "_osd", "_beep", "_buzzer",
    ];

    /// <summary>
    /// What a rarely-automated entity is docked when the wording happens to match it.
    ///
    /// A demotion rather than a ban, and sized so that one incidental token match is not enough to survive
    /// while a deliberate one is: "the garage bluetooth signal" matches on two words and still reaches the
    /// model, where "the garage light" no longer drags in every radio reading on the same device.
    /// </summary>
    private const int RarelyAutomatedPenalty = 6;

    /// <summary>
    /// A setting Home Assistant marks <c>config</c>: the LED brightness, the child lock, the power-on
    /// behaviour. Occasionally automated, so docked rather than dropped — and by less, because unlike the
    /// list above these really are things in the house that can be turned on and off.
    /// </summary>
    private const int ConfigEntityPenalty = 3;

    /// <summary>
    /// Ranks entities against a natural-language request and returns the best <paramref name="max"/>.
    /// Name matches dominate; a domain implied by the wording is a weaker signal that still gets an entity in.
    /// When the wording matches little, the list is padded with commonly automated domains so the model
    /// can use its own judgement rather than being told nothing relates.
    /// </summary>
    public static IReadOnlyList<HaEntity> Shortlist(IReadOnlyList<HaEntity> entities, string request, int max)
    {
        if (entities.Count == 0 || max <= 0) return [];

        var tokens = Tokenize(request);
        if (tokens.Count == 0)
            return Pad([], entities, max);

        HashSet<string> hinted = new(StringComparer.Ordinal);
        foreach (var token in tokens)
            if (DomainHints.TryGetValue(token, out var domains))
                foreach (var domain in domains)
                    hinted.Add(domain);

        var wanted = new HashSet<string>(tokens, StringComparer.Ordinal);

        // An entity id written out in full is an explicit instruction, not a hint. This is also what makes
        // promoting an anomaly reliable: its suggested request names the entity verbatim.
        var named = entities
            .Where(e => request.Contains(e.EntityId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.EntityId, StringComparer.Ordinal)
            .Take(max)
            .ToList();

        var explicitly = new HashSet<string>(named.Select(e => e.EntityId), StringComparer.Ordinal);

        var scored = entities
            .Where(e => !explicitly.Contains(e.EntityId))
            .Select(entity => (Entity: entity, Score: Score(entity, wanted, hinted)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Entity.EntityId, StringComparer.Ordinal)
            .Take(max - named.Count)
            .Select(x => x.Entity);

        return Pad([.. named, .. scored], entities, max);
    }

    /// <summary>
    /// Tops up a thin shortlist from the fallback domains, keeping the matched entities first.
    ///
    /// Taken a round at a time across the domains rather than in rank order. Sorting by rank and cutting at
    /// the limit drains one domain entirely before starting the next, so a house with forty lights filled
    /// the whole shortlist with lights and the model never saw a sensor, a thermostat, a lock or a cover —
    /// and the prompt then tells it that what it cannot see does not exist. Rank survives as the order the
    /// rounds are taken in, so the commonly automated domains still come first.
    /// </summary>
    private static IReadOnlyList<HaEntity> Pad(List<HaEntity> matched, IReadOnlyList<HaEntity> entities, int max)
    {
        if (matched.Count >= Math.Min(max, PadFloor)) return matched;

        var taken = new HashSet<string>(matched.Select(e => e.EntityId), StringComparer.Ordinal);
        var rank = FallbackDomains.Select((domain, index) => (domain, index)).ToDictionary(x => x.domain, x => x.index, StringComparer.Ordinal);

        // Padding is speculative by definition -- nothing here matched the request, these are offered in
        // case the model can see a use the wording did not spell out. That is precisely the budget not to
        // spend on link quality and calibration knobs: a request matching nothing in a house of thousands
        // used to be answered with a shortlist of whatever sorted first, and the prompt then tells the
        // model that what it cannot see does not exist.
        var byDomain = entities
            .Where(e => !taken.Contains(e.EntityId) && rank.ContainsKey(e.Domain) && Worth(e))
            .GroupBy(e => e.Domain, StringComparer.Ordinal)
            .OrderBy(group => rank[group.Key])
            .Select(group => group.OrderBy(e => e.EntityId, StringComparer.Ordinal).ToList())
            .ToList();

        var room = max - matched.Count;
        List<HaEntity> filler = [];

        for (var round = 0; filler.Count < room; round++)
        {
            var progressed = false;

            foreach (var domain in byDomain)
            {
                if (round >= domain.Count) continue;

                filler.Add(domain[round]);
                progressed = true;
                if (filler.Count >= room) break;
            }

            if (!progressed) break;
        }

        return [.. matched, .. filler];

        // A whole house of these would otherwise leave the shortlist empty, so the bar for padding is only
        // that the entity is something a person might mean -- not that it relates to what they asked.
        static bool Worth(HaEntity entity) =>
            !entity.Hidden &&
            !RarelyAutomated(entity) &&
            !(entity.EntityCategory is { } category && category.Equals("config", StringComparison.OrdinalIgnoreCase));
    }

    private static int Score(HaEntity entity, HashSet<string> wanted, HashSet<string> hintedDomains)
    {
        // Hidden in Home Assistant is the user saying they do not want to see this entity. Offering it to
        // the model as automation material is the same mistake as putting it back on their dashboard.
        if (entity.Hidden) return 0;

        var score = 0;

        foreach (var token in Tokenize(entity.EntityId.Replace('.', ' ').Replace('_', ' ')))
            if (wanted.Contains(token)) score += 4;

        foreach (var token in Tokenize(entity.FriendlyName))
            if (wanted.Contains(token)) score += 3;

        foreach (var token in Tokenize(entity.Area))
            if (wanted.Contains(token)) score += 3;

        if (entity.DeviceClass is not null && wanted.Contains(Singular(entity.DeviceClass.ToLowerInvariant())))
            score += 2;

        if (hintedDomains.Contains(entity.Domain))
            score += 2;

        // Applied after the matching, so wording aimed squarely at one of these still reaches it. A house
        // has far more radio readings and calibration knobs than it has lights, and every one of them in
        // the shortlist is a line not spent on something the user could plausibly have meant.
        if (RarelyAutomated(entity)) score -= RarelyAutomatedPenalty;
        else if (entity.EntityCategory is { } category && category.Equals("config", StringComparison.OrdinalIgnoreCase))
            score -= ConfigEntityPenalty;

        return Math.Max(0, score);
    }
}
