using McpEngramMemory.Core.Models.Assets;

namespace McpEngramMemory.Core.Services.Assets;

/// <summary>Deterministic, model-free curriculum dependency planner.</summary>
public static class CurriculumPlanner
{
    public static IReadOnlyList<LearningObjective> TopologicalOrder(
        IEnumerable<LearningObjective> objectives)
    {
        ArgumentNullException.ThrowIfNull(objectives);
        var byId = objectives.ToDictionary(value => value.ObjectiveId, StringComparer.Ordinal);
        var indegree = byId.Keys.ToDictionary(value => value, _ => 0, StringComparer.Ordinal);
        var dependents = byId.Keys.ToDictionary(
            value => value,
            _ => new SortedSet<string>(StringComparer.Ordinal),
            StringComparer.Ordinal);

        foreach (var objective in byId.Values)
        {
            foreach (var prerequisite in objective.PrerequisiteObjectiveIds)
            {
                if (!byId.ContainsKey(prerequisite))
                    throw new InvalidOperationException(
                        $"Objective '{objective.ObjectiveId}' references missing prerequisite '{prerequisite}'.");
                if (prerequisite == objective.ObjectiveId)
                    throw new InvalidOperationException($"Objective '{objective.ObjectiveId}' cannot depend on itself.");
                indegree[objective.ObjectiveId]++;
                dependents[prerequisite].Add(objective.ObjectiveId);
            }
        }

        var ready = new SortedSet<string>(
            indegree.Where(pair => pair.Value == 0).Select(pair => pair.Key),
            StringComparer.Ordinal);
        var ordered = new List<LearningObjective>(byId.Count);
        while (ready.Count > 0)
        {
            string id = ready.Min!;
            ready.Remove(id);
            ordered.Add(byId[id]);
            foreach (var dependent in dependents[id])
            {
                indegree[dependent]--;
                if (indegree[dependent] == 0)
                    ready.Add(dependent);
            }
        }

        if (ordered.Count != byId.Count)
            throw new InvalidOperationException("Curriculum prerequisite graph contains a cycle.");
        return ordered;
    }
}
