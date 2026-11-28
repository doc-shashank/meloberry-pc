using Meloberry.Wrapper.Domain.Models;

namespace Meloberry.Wrapper.Domain.Services;

/// <summary>
/// Single source of truth for materializing ordered spec instances from component selections
/// while preserving configured primary subtype quotas (`ModeCounts`) and active primary `Modes`.
/// </summary>
public static class SpecOrderExpander
{
    public static List<OrderedSpecInstance> Expand(
        PromptComponentPlan comp,
        bool useGlobalIndexInId = false,
        IReadOnlyList<SpecTopicItem>? specs = null)
    {
        ArgumentNullException.ThrowIfNull(comp);

        var specsByTopic = specs?.ToDictionary(s => s.Topic, StringComparer.OrdinalIgnoreCase);
        var list = new List<OrderedSpecInstance>();
        var globalIdx = 1;

        foreach (var sel in comp.Selections.Where(s => s.Enabled))
        {
            var count = Math.Clamp(sel.DesiredCount ?? 1, 1, 50);
            SpecModeGroup? primaryGroup = null;
            if (specsByTopic is not null && specsByTopic.TryGetValue(sel.Topic, out var meta))
            {
                primaryGroup = meta.ModeGroups?.FirstOrDefault();
            }

            var validPrimary = primaryGroup?.Options
                .Select(o => o.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var subtypes = new List<string?>();
            if (sel.ModeCounts is { Count: > 0 })
            {
                foreach (var (key, quota) in sel.ModeCounts)
                {
                    if (quota <= 0 || string.IsNullOrWhiteSpace(key))
                    {
                        continue;
                    }

                    if (validPrimary is not null && !validPrimary.Contains(key))
                    {
                        continue;
                    }

                    for (var q = 0; q < quota && subtypes.Count < count; q++)
                    {
                        subtypes.Add(key);
                    }
                }
            }
            else if (sel.Modes is { Count: > 0 })
            {
                var activePrimary = sel.Modes
                    .Where(m => !string.IsNullOrWhiteSpace(m) && (validPrimary is null || validPrimary.Contains(m)))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (activePrimary.Count > 0)
                {
                    for (var i = 0; i < count; i++)
                    {
                        subtypes.Add(activePrimary[i % activePrimary.Count]);
                    }
                }
            }

            for (var i = 1; i <= count; i++)
            {
                var sub = i <= subtypes.Count ? subtypes[i - 1] : null;
                var idNum = useGlobalIndexInId ? globalIdx : i;
                list.Add(new OrderedSpecInstance($"{comp.Id}_{sel.Topic}_{idNum}", sel.Topic, i, sub));
                globalIdx++;
            }
        }

        return list;
    }
}
