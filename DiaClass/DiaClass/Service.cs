using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;

namespace DiaClass;

public class Service
{
    private string _solutionFolderPath;
    private Solution _solution;
    private List<Project> _projects = new List<Project>();

    public Service(string solutionFolderPath)
    {
        _solutionFolderPath = solutionFolderPath;
    }

    public async Task Initialize()
    {
        // if no MSBuild instances are registered, register the default instance
        if (!MSBuildLocator.IsRegistered)
            MSBuildLocator.RegisterDefaults();

        using var workspace = MSBuildWorkspace.Create();

        // Log any issues Roslyn/MSBuild hit while loading
        workspace.WorkspaceFailed += (o, e) => Console.WriteLine($"[MSBuild] {e.Diagnostic}");


        #region Load Solution or Projects
        if (Path.GetExtension(_solutionFolderPath).Equals(".sln", StringComparison.OrdinalIgnoreCase))
        {
            _solution = await workspace.OpenSolutionAsync(_solutionFolderPath);
        }
        else if (Path.GetExtension(_solutionFolderPath).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            Project project = await workspace.OpenProjectAsync(_solutionFolderPath);
            _solution = project.Solution;
        }
        else
        {
            // Treat as folder:
            // Prefer a .sln if one exists; otherwise load all .csproj files
            string? sln = Directory.EnumerateFiles(_solutionFolderPath, "*.sln", SearchOption.AllDirectories).FirstOrDefault();
            if (sln is not null)
            {
                _solution = await workspace.OpenSolutionAsync(sln);
            }
            else
            {
                IEnumerable<string> projectPaths = Directory.EnumerateFiles(_solutionFolderPath, "*.csproj", SearchOption.AllDirectories);
                foreach (var proj in projectPaths)
                    workspace.OpenProjectAsync(proj); // adds to CurrentSolution
                _solution = workspace.CurrentSolution;
            }
        }

        if (_solution is null)
        {
            Console.WriteLine("No solution or C# projects found.");
            return;
        }

        Console.WriteLine("Solution correctly loaded");
        #endregion

        foreach (Project project in _solution.Projects.Where(x => x.Language == LanguageNames.CSharp))
        {
            _projects.Add(project);
        }
        Console.WriteLine($"Found {_projects.Count} C# projects");

        await ManageProjects();
    }

    public async Task ManageProjects()
    {
        Project selectedProject = SelectProject();

        // Choose how to group: logical folders (preferred) or physical directories.
        var docsByFolder = selectedProject.Documents
            .GroupBy(d => d.Folders.Count > 0 ? string.Join("/", d.Folders)
                                              : Path.GetDirectoryName(d.FilePath) ?? "")
            .OrderBy(g => g.Key);


        foreach (var folderGroup in docsByFolder)
        {
            Console.WriteLine($"\n-- Folder: {(string.IsNullOrEmpty(folderGroup.Key) ? "(root)" : folderGroup.Key)}");

            foreach (var doc in folderGroup.OrderBy(d => d.Name))
            {
                Console.WriteLine($"  File: {Path.GetFileName(doc.FilePath)}");

                CompilationUnitSyntax? root = (CompilationUnitSyntax)await doc.GetSyntaxRootAsync().ConfigureAwait(false);

                if (root is not null)
                {
                    Console.WriteLine("Root Syntax Node Information:");
                    Console.WriteLine($"Kind: {root.Kind()}");
                    Console.WriteLine($"Full Text: {root.ToFullString()}");
                    Console.WriteLine($"Child Nodes Count: {root.ChildNodes().Count()}");

                    Console.WriteLine("\nChild Nodes:");
                    foreach (var child in root.ChildNodes())
                    {
                        Console.WriteLine($" - Kind: {child.Kind()}, Text: {child.ToString().Trim()}");
                    }
                }
                else
                {
                    Console.WriteLine("Root syntax node is null.");
                }

                if (root is null) continue;

                SemanticModel? model = await doc.GetSemanticModelAsync().ConfigureAwait(false);
                if (model is null)
                {
                    Console.WriteLine("Semantic model is null.");
                    continue;
                }

                Console.WriteLine("Semantic Model Information:");
                Console.WriteLine($"Compilation: {model.Compilation.AssemblyName}");
                Console.WriteLine($"Syntax Tree: {model.SyntaxTree.FilePath}");

                foreach (var info in EnumerateTypeDeclarations(root, model))
                {
                    // Example output line
                    Console.WriteLine(
                        $"    [{info.Kind}] {info.NamespaceDisplay}{info.ContainingTypeDisplay}{info.Name} " +
                        $"{info.Modifiers} {info.Accessibility}".Trim());
                }
            }
        }

    }

    private Project SelectProject()
    {
        Console.WriteLine("Select a project:");
        for (int i = 0; i < _projects.Count; i++)
        {
            Console.WriteLine($" - {_projects[i].Name}");
        }

        while (true)
        {
            string? input = Console.ReadLine();
            if (input is null) continue;
            Project? project = _projects.FirstOrDefault(p => p.Name.Equals(input, StringComparison.OrdinalIgnoreCase));
            if (project is not null)
            {
                Console.WriteLine($"Project '{project.Name}' selected.");
                return project;
            }
            else
            {
                Console.WriteLine("Project not found. Please try again.");
            }
        }
    }




    private static IEnumerable<TypeInfoRow> EnumerateTypeDeclarations(CompilationUnitSyntax root, SemanticModel model)
    {
        var typeNodes = root.DescendantNodes().Where(n =>
               n is ClassDeclarationSyntax
            || n is InterfaceDeclarationSyntax
            || n is EnumDeclarationSyntax
            || n is StructDeclarationSyntax
            || n is RecordDeclarationSyntax);

        foreach (var node in typeNodes)
        {
            ISymbol? symbol = node switch
            {
                ClassDeclarationSyntax cls => model.GetDeclaredSymbol(cls),
                InterfaceDeclarationSyntax i => model.GetDeclaredSymbol(i),
                EnumDeclarationSyntax en => model.GetDeclaredSymbol(en),
                StructDeclarationSyntax st => model.GetDeclaredSymbol(st),
                RecordDeclarationSyntax rec => model.GetDeclaredSymbol(rec),
                _ => null
            };

            if (symbol is null) continue;

            yield return new TypeInfoRow
            {
                Kind = symbol.Kind switch
                {
                    SymbolKind.NamedType => ((INamedTypeSymbol)symbol).TypeKind switch
                    {
                        TypeKind.Class => "class",
                        TypeKind.Struct => "struct",
                        TypeKind.Interface => "interface",
                        TypeKind.Enum => "enum",
                        TypeKind.Delegate => "delegate",
                        _ => "type"
                    },
                    _ => symbol.Kind.ToString().ToLowerInvariant()
                },
                Name = symbol.Name,
                NamespaceDisplay = GetNamespacePrefix(symbol),
                ContainingTypeDisplay = GetContainingTypePrefix(symbol),
                Accessibility = symbol.DeclaredAccessibility.ToString().ToLowerInvariant(),
                Modifiers = GetModifiers(node)
            };
        }
    }

    private static string GetNamespacePrefix(ISymbol symbol)
    {
        var ns = symbol.ContainingNamespace;
        if (ns == null || ns.IsGlobalNamespace) return "";
        return ns.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) + ".";
    }

    private static string GetContainingTypePrefix(ISymbol symbol)
    {
        var stack = new Stack<string>();
        var t = symbol.ContainingType;
        while (t != null)
        {
            stack.Push(t.Name);
            t = t.ContainingType;
        }
        return stack.Count > 0 ? string.Join(".", stack) + "." : "";
    }

    private static string GetModifiers(SyntaxNode node)
    {
        SyntaxTokenList mods = node switch
        {
            BaseTypeDeclarationSyntax td => td.Modifiers,
            //EnumDeclarationSyntax ed => ed.Modifiers,
            _ => default
        };
        // Keep a small, readable subset
        var list = mods.Where(m =>
                m.IsKind(SyntaxKind.PartialKeyword) ||
                m.IsKind(SyntaxKind.StaticKeyword) ||
                m.IsKind(SyntaxKind.AbstractKeyword) ||
                m.IsKind(SyntaxKind.SealedKeyword) ||
                m.IsKind(SyntaxKind.ReadOnlyKeyword))
            .Select(m => m.Text);
        var s = string.Join(" ", list);
        return string.IsNullOrWhiteSpace(s) ? "" : $"({s})";
    }

    private record TypeInfoRow
    {
        public string Kind { get; init; } = "";
        public string Name { get; init; } = "";
        public string NamespaceDisplay { get; init; } = "";
        public string ContainingTypeDisplay { get; init; } = "";
        public string Accessibility { get; init; } = "";
        public string Modifiers { get; init; } = "";
    }
}
