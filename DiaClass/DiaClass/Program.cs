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

        var graph = await service.ExtractRelationsAsync(projectName: "DiaClass", onlyInternalToProject: true);

        // Mermaid you can paste into docs / markdown renderers:
        var mermaid = graph.ToMermaid();
        // write to a file if you want
        File.WriteAllText("diagram.mmd", mermaid);

    }
}
