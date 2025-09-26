using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace DiaClass;

public sealed class Service
{
    private readonly string _solutionFolderPath;
    private Solution? _solution;
    private readonly List<Project> _projects = new();

    public Service(string solutionFolderPath)
    {
        _solutionFolderPath = solutionFolderPath;
    }

    /// <summary>
    /// Loads the solution or projects from the provided path.
    /// Optionally pass a project name later to work on a specific project.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (!MSBuildLocator.IsRegistered)
            MSBuildLocator.RegisterDefaults();

        using var workspace = MSBuildWorkspace.Create();
        workspace.WorkspaceFailed += (_, __) => { /* swallow diagnostics or hook up your logger */ };

        _solution = await OpenSolutionOrProjectsAsync(workspace, _solutionFolderPath);
        if (_solution is null) return;

        _projects.Clear();
        _projects.AddRange(_solution.Projects.Where(p => p.Language == LanguageNames.CSharp));
    }

    /// <summary>
    /// Returns all C# projects discovered after InitializeAsync().
    /// </summary>
    public IReadOnlyList<Project> GetProjects() => _projects;

    /// <summary>
    /// Extracts type relations for a given project.
    /// If projectName is null, the first C# project is used.
    /// </summary>
    public async Task<RelationGraph> ExtractRelationsAsync(string? projectName = null, bool onlyInternalToProject = true)
    {
        if (_solution is null || _projects.Count == 0)
            throw new InvalidOperationException("Call InitializeAsync() first and ensure there are C# projects.");

        var project = projectName is null
            ? _projects[0]
            : _projects.FirstOrDefault(p => string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase))
              ?? throw new ArgumentException($"Project '{projectName}' not found.");

        var compilation = await project.GetCompilationAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("Compilation could not be created.");

        var graph = new RelationGraph(project.AssemblyName ?? project.Name);

        // Collect all named types from the compilation (classes, structs, records, interfaces, enums)
        foreach (var type in GetAllNamedTypes(compilation.Assembly.GlobalNamespace))
        {
            // Optionally scope to types declared in this project's assembly
            if (onlyInternalToProject && !SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, compilation.Assembly))
                continue;

            var source = Display(type);

            // Contains (nested types)
            if (type.ContainingType is INamedTypeSymbol ct)
            {
                var parent = Display(ct);
                if (IncludeType(ct, compilation, onlyInternalToProject))
                    graph.AddEdge(parent, source, RelationKind.Contains);
            }

            // Inheritance (base type)
            if (type.TypeKind is TypeKind.Class or TypeKind.Struct)
            {
                var baseType = type.BaseType;
                if (baseType is not null &&
                    baseType.SpecialType != SpecialType.System_Object &&
                    IncludeType(baseType, compilation, onlyInternalToProject))
                {
                    graph.AddEdge(source, Display(baseType), RelationKind.Inherits);
                }
            }

            // Implements (interfaces, including through base)
            foreach (var i in type.Interfaces)
            {
                if (IncludeType(i, compilation, onlyInternalToProject))
                    graph.AddEdge(source, Display(i), RelationKind.Implements);
            }

            // Members → usage edges
            foreach (var m in type.GetMembers())
            {
                switch (m)
                {
                    case IFieldSymbol f when IncludeType(f.Type, compilation, onlyInternalToProject):
                        graph.AddEdge(source, Display(NormalizeNullable(f.Type)), RelationKind.FieldUses);
                        break;

                    case IPropertySymbol p when IncludeType(p.Type, compilation, onlyInternalToProject):
                        graph.AddEdge(source, Display(NormalizeNullable(p.Type)), RelationKind.PropertyUses);
                        break;

                    case IMethodSymbol method:
                        {
                            // Return type
                            if (method.MethodKind != MethodKind.PropertyGet &&
                                method.MethodKind != MethodKind.PropertySet &&
                                method.ReturnType.SpecialType != SpecialType.System_Void &&
                                IncludeType(method.ReturnType, compilation, onlyInternalToProject))
                            {
                                graph.AddEdge(source, Display(NormalizeNullable(method.ReturnType)), RelationKind.MethodReturns);
                            }

                            // Parameters
                            foreach (var p in method.Parameters)
                            {
                                if (IncludeType(p.Type, compilation, onlyInternalToProject))
                                    graph.AddEdge(source, Display(NormalizeNullable(p.Type)), RelationKind.MethodParameter);
                            }

                            break;
                        }
                }
            }
        }

        return graph;
    }

    // ----------------- Helpers -----------------

    private static async Task<Solution?> OpenSolutionOrProjectsAsync(MSBuildWorkspace workspace, string path)
    {
        if (File.Exists(path))
        {
            var ext = Path.GetExtension(path);
            if (ext.Equals(".sln", StringComparison.OrdinalIgnoreCase))
                return await workspace.OpenSolutionAsync(path);

            if (ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
                return (await workspace.OpenProjectAsync(path)).Solution;
        }

        var sln = Directory.EnumerateFiles(path, "*.sln", SearchOption.AllDirectories).FirstOrDefault();
        if (sln is not null) return await workspace.OpenSolutionAsync(sln);

        foreach (var csproj in Directory.EnumerateFiles(path, "*.csproj", SearchOption.AllDirectories))
            await workspace.OpenProjectAsync(csproj);

        return workspace.CurrentSolution;
    }

    private static IEnumerable<INamedTypeSymbol> GetAllNamedTypes(INamespaceSymbol ns)
    {
        foreach (var member in ns.GetMembers())
        {
            if (member is INamespaceSymbol childNs)
            {
                foreach (var t in GetAllNamedTypes(childNs))
                    yield return t;
            }
            else if (member is INamedTypeSymbol t)
            {
                foreach (var nt in FlattenNestedTypes(t))
                    yield return nt;
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> FlattenNestedTypes(INamedTypeSymbol t)
    {
        yield return t;
        foreach (var n in t.GetTypeMembers())
        {
            foreach (var nt in FlattenNestedTypes(n))
                yield return nt;
        }
    }

    private static bool IncludeType(ITypeSymbol type, Compilation compilation, bool onlyInternal)
    {
        var nt = (type as INamedTypeSymbol)?.ConstructedFrom ?? type as INamedTypeSymbol;
        if (nt is null) return false;

        // Skip primitives and special types
        if (nt.SpecialType != SpecialType.None) return false;

        // Optionally restrict to this assembly
        return !onlyInternal || SymbolEqualityComparer.Default.Equals(nt.ContainingAssembly, compilation.Assembly);
    }

    private static ITypeSymbol NormalizeNullable(ITypeSymbol t) =>
        t is INamedTypeSymbol n && n.OriginalDefinition.SpecialType is SpecialType.System_Nullable_T
            ? n.TypeArguments[0]
            : t;

    private static string Display(ISymbol s) =>
        s.ToDisplayString(new SymbolDisplayFormat(
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters));

    // ----------------- Public DTOs -----------------

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

    public sealed class Relation
    {
        public string From { get; init; } = "";
        public string To { get; init; } = "";
        public RelationKind Kind { get; init; }
    }

    public sealed class RelationGraph
    {
        public string Scope { get; }
        public IReadOnlyCollection<string> Nodes => _nodes;
        public IReadOnlyCollection<Relation> Edges => _edges;

        private readonly HashSet<string> _nodes = new();
        private readonly List<Relation> _edges = new();

        public RelationGraph(string scope) => Scope = scope;

        public void AddEdge(string from, string to, RelationKind kind)
        {
            if (from == to) return; // ignore self loops
            _nodes.Add(from);
            _nodes.Add(to);
            _edges.Add(new Relation { From = from, To = to, Kind = kind });
        }

        /// <summary>
        /// Produces a Mermaid class diagram (simple and readable).
        /// </summary>
        public string ToMermaid()
        {
            // Group edges by kind for readable arrows
            // inherits: <|--, implements: <|..
            // uses: ..> (with labels)
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("classDiagram");

            // Declare classes
            foreach (var n in _nodes.OrderBy(s => s))
            {
                var id = Escape(n);
                sb.AppendLine($"class {id}");
            }

            foreach (var e in _edges)
            {
                var a = Escape(e.From);
                var b = Escape(e.To);
                var line = e.Kind switch
                {
                    RelationKind.Inherits => $"{b} <|-- {a}",
                    RelationKind.Implements => $"{b} <|.. {a}",
                    RelationKind.Contains => $"{a} *-- {b}",
                    RelationKind.FieldUses => $"{a} ..> {b} : field",
                    RelationKind.PropertyUses => $"{a} ..> {b} : property",
                    RelationKind.MethodReturns => $"{a} ..> {b} : returns",
                    RelationKind.MethodParameter => $"{a} ..> {b} : param",
                    _ => $"{a} ..> {b}"
                };
                sb.AppendLine(line);
            }

            return sb.ToString();

            static string Escape(string s)
                => s.Replace('<', '_').Replace('>', '_').Replace('.', '_').Replace('+', '_').Replace(',', '_').Replace(' ', '_');
        }
    }
}
