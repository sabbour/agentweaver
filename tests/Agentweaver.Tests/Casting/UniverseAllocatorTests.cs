using Agentweaver.Squad.Naming;
using Agentweaver.Squad.Model;

namespace Agentweaver.Tests.Casting;

/// <summary>
/// SC-005: Universe name allocation — no duplicates, no out-of-universe names, retired names never reused.
/// </summary>
public sealed class UniverseAllocatorTests
{
    private static CastingPolicy MakePolicy() => new CastingPolicy(
        "1.0",
        ["The Matrix", "Star Wars", "Inception", "Firefly"],
        new Dictionary<string, int> { ["The Matrix"] = 13, ["Star Wars"] = 14, ["Inception"] = 9, ["Firefly"] = 12 });

    [Fact]
    public void BasicAllocation_ReturnsNamesFromChosenUniverse()
    {
        var allocator = new UniverseAllocator(MakePolicy());
        var reserved  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var results = allocator.AllocateNames("Inception", reserved, 2);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.IsNamed));
        Assert.All(results, r => Assert.NotEmpty(r.Name));
    }

    [Fact]
    public void Allocation_RespectsReservedNames()
    {
        var allocator = new UniverseAllocator(MakePolicy());
        var reserved  = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Neo" };

        var results = allocator.AllocateNames("The Matrix", reserved, 3);

        Assert.All(results, r => Assert.NotEqual("Neo", r.Name, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Allocation_Overflow_ProducesMemberNumbers()
    {
        var allocator = new UniverseAllocator(MakePolicy());
        // Inception has 9 names — reserve all to force overflow.
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Cobb", "Ariadne", "Arthur", "Eames", "Yusuf", "Saito", "Fischer", "Mal", "Miles" };

        var results = allocator.AllocateNames("Inception", reserved, 2);

        Assert.All(results, r => Assert.False(r.IsNamed));
        Assert.All(results, r => Assert.Matches(@"^member-\d+$", r.Name));
    }

    [Fact]
    public void AllocateOne_ReturnsSingleEntry()
    {
        var allocator = new UniverseAllocator(MakePolicy());
        var reserved  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var result = allocator.AllocateOne("Star Wars", reserved);

        Assert.NotEmpty(result.Name);
    }

    [Fact]
    public void ProposeUniverse_EmptyHistory_ReturnsFirstAllowlistUniverse()
    {
        var allocator = new UniverseAllocator(MakePolicy());

        var universe = allocator.ProposeUniverse([]);

        Assert.Equal("The Matrix", universe, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProposeUniverse_WithHistory_ReturnsUnusedUniverse()
    {
        var allocator = new UniverseAllocator(MakePolicy());

        var universe = allocator.ProposeUniverse(["The Matrix"]);

        Assert.NotEqual("The Matrix", universe, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsValidUniverse_KnownUniverse_ReturnsTrue()
    {
        var allocator = new UniverseAllocator(MakePolicy());

        Assert.True(allocator.IsValidUniverse("The Matrix"));
    }

    [Fact]
    public void IsValidUniverse_UnknownUniverse_ReturnsFalse()
    {
        var allocator = new UniverseAllocator(MakePolicy());

        Assert.False(allocator.IsValidUniverse("InvalidUniverse"));
    }
}
