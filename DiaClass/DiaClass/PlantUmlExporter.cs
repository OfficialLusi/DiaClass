using System.Text;

namespace DiaClass;

public static class PlantUmlExporter
{
    // Map your relation kinds to PlantUML arrows
    private static string Arrow(RelationKind k) => k switch
    {
        RelationKind.Inherits => "<|--",
        RelationKind.Implements => "<|..",
        RelationKind.Contains => "*--",
        RelationKind.FieldUses => "..>",
        RelationKind.PropertyUses => "..>",
        RelationKind.MethodReturns => "..>",
        RelationKind.MethodParameter => "..>",
        _ => "..>"
    };

    /// <summary>
    /// Full class diagram. Supports: kind filter, short names, grouping into packages,
    /// and showing multiplicity counts on "uses" edges.
    /// </summary>
    public static string ToPlantUml(
        RelationGraph g,
        HashSet<RelationKind>? includeKinds = null,
        Func<string, string>? shortName = null,
        Func<string, string?>? packageOf = null,   // return package name or null for no package
        bool showCountsOnUses = true)
    {
        includeKinds ??= new HashSet<RelationKind>(Enum.GetValues<RelationKind>());
        shortName ??= DefaultShort;

        // Pre-build node -> alias + display text
        var aliases = g.Nodes.ToDictionary(
            n => n,
            n => new
            {
                Alias = Alias(n),
                Display = shortName(n),
                Package = packageOf?.Invoke(n)
            });

        var sb = new StringBuilder();
        sb.AppendLine("@startuml");
        sb.AppendLine("skinparam classAttributeIconSize 0");
        sb.AppendLine("skinparam dpi 160"); // crisper PNGs

        // Declare packages (if requested)
        if (packageOf is not null)
        {
            foreach (var grp in aliases.Values.Where(v => v.Package is not null).GroupBy(v => v.Package!))
            {
                sb.AppendLine($"package \"{grp.Key}\" {{");
                foreach (var v in grp)
                    sb.AppendLine($"  class \"{v.Display}\" as {v.Alias}");
                sb.AppendLine("}");
            }
            // Orphan nodes without a package
            foreach (var v in aliases.Values.Where(v => v.Package is null))
                sb.AppendLine($"class \"{v.Display}\" as {v.Alias}");
        }
        else
        {
            foreach (var v in aliases.Values)
                sb.AppendLine($"class \"{v.Display}\" as {v.Alias}");
        }

        // Edges (deduped & counted already by RelationGraph)
        foreach (var (e, count) in g.CountedEdges)
        {
            if (!includeKinds.Contains(e.Kind)) continue;

            var from = aliases[e.From].Alias;
            var to = aliases[e.To].Alias;
            var arrow = Arrow(e.Kind);

            string label = e.Kind switch
            {
                RelationKind.FieldUses => "field",
                RelationKind.PropertyUses => "property",
                RelationKind.MethodReturns => "returns",
                RelationKind.MethodParameter => "param",
                _ => ""
            };

            var line = new StringBuilder();
            line.Append(from).Append(' ').Append(arrow).Append(' ').Append(to);
            if (label.Length > 0)
            {
                line.Append(" : ").Append(label);
                if (showCountsOnUses && count > 1) line.Append(" ×").Append(count);
            }
            sb.AppendLine(line.ToString());
        }

        sb.AppendLine("@enduml");
        return sb.ToString();

        static string DefaultShort(string full)
        {
            // last two segments: Namespace.Type
            var parts = full.Split('.');
            return parts.Length >= 2 ? $"{parts[^2]}.{parts[^1]}" : full;
        }
        static string Alias(string s) =>
            s.Replace('<', '_').Replace('>', '_').Replace('.', '_').Replace('+', '_').Replace(',', '_').Replace(' ', '_');
    }

    /// <summary>
    /// Very compact 3-box inter-context overview (Domain / Application / Infrastructure).
    /// Counts cross-context edges and prints a single arrow with totals.
    /// </summary>
    public static string ToPlantUmlInterContext(RelationGraph g, Func<string, string> contextOf,
                                                bool usesOnly = true, bool showCounts = true)
    {
        var uses = new HashSet<RelationKind>
    {
        RelationKind.FieldUses, RelationKind.PropertyUses,
        RelationKind.MethodParameter, RelationKind.MethodReturns
    };

        var counts = new Dictionary<(string From, string To, string Kind), int>();
        foreach (var (e, c) in g.CountedEdges)
        {
            if (usesOnly && !uses.Contains(e.Kind)) continue;

            var fromC = contextOf(e.From);
            var toC = contextOf(e.To);
            if (fromC == toC) continue;

            var key = (fromC, toC, e.Kind.ToString());
            counts[key] = counts.TryGetValue(key, out var n) ? n + c : c;
        }

        var sb = new StringBuilder();
        sb.AppendLine("@startuml");
        sb.AppendLine("skinparam dpi 160");
        var ctxs = new HashSet<string>(counts.Keys.SelectMany(k => new[] { k.From, k.To }));
        foreach (var c in ctxs) sb.AppendLine($@"rectangle ""{c}"" as {c}");

        foreach (var ((from, to, kind), n) in counts.OrderBy(x => x.Key.From).ThenBy(x => x.Key.To))
        {
            var label = usesOnly ? $"×{n}" : $"{kind} ×{n}";
            sb.AppendLine($"{from} ..> {to} : {label}");
        }

        sb.AppendLine("@enduml");
        return sb.ToString();
    }
}