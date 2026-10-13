using Xunit;

namespace Agentic.Orchestration.Tests;

public class GraphTests
{
    [Fact]
    public void Rejects_cycles()
    {
        var ex = Assert.Throws<InvalidWorkflowException>(() => new WorkflowGraph("g", new[]
        {
            T.Node("a", "x", dependsOn: new[] { "b" }), T.Node("b", "x", dependsOn: new[] { "a" }),
        }));
        Assert.Contains("cycle", ex.Message);
    }

    [Fact]
    public void Rejects_unknown_dependency_and_duplicates()
    {
        Assert.Throws<InvalidWorkflowException>(() => new WorkflowGraph("g", new[] { T.Node("a", "x", dependsOn: new[] { "zzz" }) }));
        Assert.Throws<InvalidWorkflowException>(() => new WorkflowGraph("g", new[] { T.Node("a", "x"), T.Node("a", "x") }));
    }

    [Fact]
    public void High_impact_node_requires_an_approval_checkpoint()
    {
        var ex = Assert.Throws<InvalidWorkflowException>(() => new WorkflowGraph("g", new[] { T.Node("deploy", "x", impact: Impact.High) }));
        Assert.Contains("change-control", ex.Message);
    }

    [Fact]
    public void Consumed_artifact_must_have_an_upstream_producer()
    {
        Assert.Throws<InvalidWorkflowException>(() => new WorkflowGraph("g", new[]
        {
            T.Node("a", "x", produces: new[] { "doc" }), T.Node("b", "x", consumes: new[] { "doc" }),
        }));
        // declaring the dependency fixes it
        _ = new WorkflowGraph("g", new[]
        {
            T.Node("a", "x", produces: new[] { "doc" }), T.Node("b", "x", dependsOn: new[] { "a" }, consumes: new[] { "doc" }),
        });
    }

    [Fact]
    public void Waves_group_parallel_branches_and_joins()
    {
        var g = new WorkflowGraph("g", new[]
        {
            T.Node("a", "x"), T.Node("b", "x", dependsOn: new[] { "a" }), T.Node("c", "x", dependsOn: new[] { "a" }),
            T.Node("d", "x", dependsOn: new[] { "b", "c" }),
        });
        var waves = g.Waves();
        Assert.Equal(3, waves.Count);
        Assert.Equal(new[] { "b", "c" }, waves[1]);
        Assert.Contains("a --> b", g.ToMermaid());
    }
}
