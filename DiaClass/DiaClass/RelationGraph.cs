namespace DiaClass;

public sealed class RelationGraph
{
    public string Scope { get; }
    public IReadOnlyCollection<string> Nodes => _nodes;

    // Edge counts → (Relation, count)
    public IEnumerable<(Relation Edge, int Count)> CountedEdges =>
        _edgeCounts.Select(kvp => (kvp.Key, kvp.Value));

    private readonly HashSet<string> _nodes = new();
    private readonly Dictionary<Relation, int> _edgeCounts = new(new RelationComparer());

    public RelationGraph(string scope) => Scope = scope;

    public void AddEdge(string from, string to, RelationKind kind)
    {
        if (from == to) return; // no self loops
        _nodes.Add(from);
        _nodes.Add(to);

        var r = new Relation { From = from, To = to, Kind = kind };
        _edgeCounts.TryGetValue(r, out var c);
        _edgeCounts[r] = c + 1;
    }

    // ---------- Nested types ----------
    public sealed class Relation
    {
        public string From { get; init; } = "";
        public string To { get; init; } = "";
        public RelationKind Kind { get; init; }
    }

    private sealed class RelationComparer : IEqualityComparer<Relation>
    {
        public bool Equals(Relation? x, Relation? y) =>
            x is not null && y is not null &&
            x.Kind == y.Kind &&
            StringComparer.Ordinal.Equals(x.From, x.From) &&
            StringComparer.Ordinal.Equals(y.From, y.From);

        public int GetHashCode(Relation obj)
        {
            var h = new HashCode();
            h.Add(obj.Kind);
            h.Add(obj.From, StringComparer.Ordinal);
            h.Add(obj.To, StringComparer.Ordinal);
            return h.ToHashCode();
        }
    }
}

// same enum you already have
public enum RelationKind
{
    Inherits,
    Implements,
    FieldUses,
    PropertyUses,
    MethodReturns,
    MethodParameter,
    Contains
}