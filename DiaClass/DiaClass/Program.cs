using Microsoft.CodeAnalysis;

namespace DiaClass;

public class Program
{
    public static async Task Main(string[] args) // Define Main as async
    {
        Console.WriteLine("# ----------------------------- DiaClass ----------------------------- #");
        Console.WriteLine("# ------------------ A tool to visualize C# projects ----------------- #");
        //Console.WriteLine("# --------------------------- Author: Lusi --------------------------- #");
        Console.WriteLine("# -------------------------------------------------------------------- #");

        Console.WriteLine("Insert here the path to your .sln folder:\n\n");

        string path;

        while (true)
        {
            path = Console.ReadLine();
            if (Loader.CheckPath(path))
            {
                Console.WriteLine("\nPath is valid!\n");
                break;
            }
            else
                Console.WriteLine("\nPath is not valid, please reinsert it:\n");
        }

        Service service = new Service(path);

        await service.InitializeAsync();

        var graph = await service.ExtractRelationsAsync(projectName: "MeltField", onlyInternalToProject: true);

        // (a) Per-context diagrams (group into packages by context)
        string ContextOf(string full)
        {
            var p = full.Split('.');
            var i = Array.FindIndex(p, x => x is "Domain" or "Application" or "Infrastructure");
            return i >= 0 ? p[i] : "Other";
        }
        string? PackageOf(string full) => ContextOf(full);

        // Structure only (inheritance/implements/contains) per project
        var structureKinds = new HashSet<RelationKind>
        {
            RelationKind.Inherits,
            RelationKind.Implements,
            RelationKind.Contains
        };

        File.WriteAllText("project-structure.puml",
            PlantUmlExporter.ToPlantUml(graph, includeKinds: structureKinds,
                                        shortName: s => s.Split('.').Last(),
                                        packageOf: PackageOf));

        // (b) Inter-context overview (3 boxes with counts)
        File.WriteAllText("inter-context.puml",
            PlantUmlExporter.ToPlantUmlInterContext(graph, ContextOf, usesOnly: true));

    }
}
