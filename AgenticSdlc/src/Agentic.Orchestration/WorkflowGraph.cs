using System.Text;

namespace Agentic.Orchestration;

/// <summary>
/// Explicit dependency graph. Construction fails fast on: duplicate ids, unknown dependencies, cycles,
/// consumed artifacts without an upstream producer, and high-impact nodes lacking an approval checkpoint.
/// </summary>
public sealed class WorkflowGraph
{
    public WorkflowGraph(string name, IEnumerable<NodeSpec> nodes, IEnumerable<string>? externalInputs = null)
    {
        Name = name;
        ExternalInputs = (externalInputs ?? Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var dict = new Dictionary<string, NodeSpec>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (!dict.TryAdd(node.Id, node))
            {
                throw new InvalidWorkflowException($"duplicate node id '{node.Id}'");
            }
        }

        foreach (var node in dict.Values)
        {
            foreach (var dep in node.DependsOn)
            {
                if (!dict.ContainsKey(dep))
                {
                    throw new InvalidWorkflowException($"node '{node.Id}' depends on unknown node '{dep}'");
                }
            }

            if (node.Impact == Impact.High && node.Approval == ApprovalMode.None)
            {
                throw new InvalidWorkflowException($"change-control: high-impact node '{node.Id}' must declare a human approval checkpoint");
            }
        }

        Nodes = dict;
        TopologicalOrder = ComputeOrder();

        var producers = dict.Values
            .SelectMany(n => n.Produces.Select(p => (Artifact: p, Node: n.Id)))
            .ToLookup(x => x.Artifact, x => x.Node, StringComparer.OrdinalIgnoreCase);

        foreach (var node in dict.Values)
        {
            var ancestors = Ancestors(node.Id);
            foreach (var consumed in node.Consumes)
            {
                if (ExternalInputs.Contains(consumed))
                {
                    continue;
                }

                if (!producers[consumed].Any(p => ancestors.Contains(p)))
                {
                    throw new InvalidWorkflowException($"node '{node.Id}' consumes '{consumed}' but no upstream node produces it");
                }
            }
        }
    }

    public string Name { get; }
    public IReadOnlyDictionary<string, NodeSpec> Nodes { get; }
    public IReadOnlySet<string> ExternalInputs { get; }
    public IReadOnlyList<string> TopologicalOrder { get; }

    public IReadOnlySet<string> Ancestors(string id)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>(Nodes[id].DependsOn);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (result.Add(current))
            {
                foreach (var dep in Nodes[current].DependsOn)
                {
                    stack.Push(dep);
                }
            }
        }

        return result;
    }

    public IEnumerable<string> Dependents(string id) =>
        Nodes.Values.Where(n => n.DependsOn.Contains(id)).Select(n => n.Id);

    /// <summary>Execution waves: nodes in the same wave have no mutual dependency and may run in parallel.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Waves()
    {
        var depth = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var id in TopologicalOrder)
        {
            depth[id] = Nodes[id].DependsOn.Count == 0 ? 0 : Nodes[id].DependsOn.Max(d => depth[d]) + 1;
        }

        return depth.GroupBy(kv => kv.Value).OrderBy(g => g.Key)
            .Select(g => (IReadOnlyList<string>)g.Select(kv => kv.Key).OrderBy(s => s, StringComparer.Ordinal).ToList())
            .ToList();
    }

    public string ToMermaid()
    {
        var sb = new StringBuilder("flowchart TD\n");
        foreach (var node in Nodes.Values)
        {
            var label = node.Approval == ApprovalMode.None ? node.Id : $"{node.Id}<br/>(approval: {node.Approval})";
            sb.AppendLine($"  {Safe(node.Id)}[\"{label}\"]");
            if (node.Impact == Impact.High)
            {
                sb.AppendLine($"  style {Safe(node.Id)} stroke:#c0392b,stroke-width:2px");
            }
        }

        foreach (var node in Nodes.Values)
        {
            foreach (var dep in node.DependsOn)
            {
                sb.AppendLine($"  {Safe(dep)} --> {Safe(node.Id)}");
            }
        }

        return sb.ToString();
    }

    private static string Safe(string id) => id.Replace('-', '_');

    private IReadOnlyList<string> ComputeOrder()
    {
        var inDegree = Nodes.Values.ToDictionary(n => n.Id, n => n.DependsOn.Count, StringComparer.Ordinal);
        var queue = new Queue<string>(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key).OrderBy(s => s, StringComparer.Ordinal));
        var order = new List<string>();
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            order.Add(id);
            foreach (var dependent in Dependents(id).OrderBy(s => s, StringComparer.Ordinal))
            {
                if (--inDegree[dependent] == 0)
                {
                    queue.Enqueue(dependent);
                }
            }
        }

        if (order.Count != Nodes.Count)
        {
            var cyclic = inDegree.Where(kv => kv.Value > 0).Select(kv => kv.Key);
            throw new InvalidWorkflowException($"dependency cycle involving: {string.Join(", ", cyclic)}");
        }

        return order;
    }
}
