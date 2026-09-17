using System.Globalization;

namespace Housekeeper.Core;

/// <summary>
/// Flags existing automations that look like they already do what a draft proposes. Deterministic on
/// purpose: it costs nothing, it is testable, and it runs before the user is asked to confirm.
/// </summary>
public static class DuplicateFinder
{
    public const double DefaultThreshold = 0.45;

    private const double EntityWeight = 0.60;
    private const double TriggerWeight = 0.25;
    private const double AliasWeight = 0.15;

    public static IReadOnlyList<DuplicateMatch> Find(
        AutomationDraft draft,
        IEnumerable<ExistingAutomation> existing,
        double threshold = DefaultThreshold)
    {
        var draftEntities = new HashSet<string>(draft.Entities, StringComparer.Ordinal);
        var draftAlias = new HashSet<string>(EntityIndex.Tokenize(draft.Alias), StringComparer.Ordinal);

        List<DuplicateMatch> matches = [];

        foreach (var candidate in existing)
        {
            var entityOverlap = Jaccard(draftEntities, candidate.Entities);
            var triggerOverlap = Jaccard(draft.TriggerKinds, candidate.TriggerKinds);
            var aliasOverlap = Jaccard(draftAlias, new HashSet<string>(EntityIndex.Tokenize(candidate.Alias), StringComparer.Ordinal));

            var score = (EntityWeight * entityOverlap) + (TriggerWeight * triggerOverlap) + (AliasWeight * aliasOverlap);
            if (score < threshold) continue;

            matches.Add(new DuplicateMatch(
                candidate.Id,
                candidate.Alias,
                Math.Round(score, 3),
                Describe(draftEntities, candidate, entityOverlap, triggerOverlap, aliasOverlap)));
        }

        return [.. matches.OrderByDescending(m => m.Score).ThenBy(m => m.Alias, StringComparer.Ordinal)];
    }

    private static string Describe(
        IReadOnlySet<string> draftEntities,
        ExistingAutomation candidate,
        double entityOverlap,
        double triggerOverlap,
        double aliasOverlap)
    {
        List<string> parts = [];

        var shared = draftEntities.Intersect(candidate.Entities, StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        if (shared.Count > 0)
            parts.Add($"shares {shared.Count} entity/entities ({string.Join(", ", shared.Take(3))}{(shared.Count > 3 ? ", …" : "")})");

        if (triggerOverlap > 0)
            parts.Add("triggers the same way");

        if (aliasOverlap > 0)
            parts.Add("has a similar name");

        if (parts.Count == 0)
            parts.Add($"scored {entityOverlap.ToString("0.00", CultureInfo.InvariantCulture)} on entity overlap");

        return char.ToUpperInvariant(parts[0][0]) + parts[0][1..] +
               (parts.Count > 1 ? " and " + string.Join(", ", parts.Skip(1)) : "") + ".";
    }

    private static double Jaccard(IReadOnlySet<string> left, IReadOnlySet<string> right)
    {
        if (left.Count == 0 || right.Count == 0) return 0;

        var intersection = left.Count(right.Contains);
        var union = left.Count + right.Count - intersection;
        return union == 0 ? 0 : (double)intersection / union;
    }
}
