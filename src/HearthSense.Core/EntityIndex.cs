namespace HearthSense.Core;

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

    /// <summary>Tops up a thin shortlist from the fallback domains, keeping the matched entities first.</summary>
    private static IReadOnlyList<HaEntity> Pad(List<HaEntity> matched, IReadOnlyList<HaEntity> entities, int max)
    {
        if (matched.Count >= Math.Min(max, PadFloor)) return matched;

        var taken = new HashSet<string>(matched.Select(e => e.EntityId), StringComparer.Ordinal);
        var rank = FallbackDomains.Select((domain, index) => (domain, index)).ToDictionary(x => x.domain, x => x.index, StringComparer.Ordinal);

        var filler = entities
            .Where(e => !taken.Contains(e.EntityId) && rank.ContainsKey(e.Domain))
            .OrderBy(e => rank[e.Domain])
            .ThenBy(e => e.EntityId, StringComparer.Ordinal)
            .Take(max - matched.Count);

        return [.. matched, .. filler];
    }

    private static int Score(HaEntity entity, HashSet<string> wanted, HashSet<string> hintedDomains)
    {
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

        return score;
    }
}
