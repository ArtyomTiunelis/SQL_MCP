using System.Text.Json;
using System.Text.Json.Nodes;

namespace SQL_MCP;

public static class ManifestService
{
    private static readonly string ManifestPath =
        Path.Combine(AppContext.BaseDirectory, "mcp-manifest.json");

    private static readonly JsonSerializerOptions PrettyPrint = new() { WriteIndented = true };

    public static bool HandleDiscoveryArgs(string[] args)
    {
        if (args.Length == 0 || (args[0] != "--list-tools" && args[0] != "--info"))
            return false;

        if (!File.Exists(ManifestPath))
        {
            Console.Error.WriteLine($"Manifest file not found at: {ManifestPath}");
            return true;
        }

        var manifest = JsonNode.Parse(File.ReadAllText(ManifestPath))!;

        if (args[0] == "--list-tools")
        {
            var tools = manifest["tools"];
            Console.WriteLine(tools!.ToJsonString(PrettyPrint));
        }
        else
        {
            Console.WriteLine(manifest.ToJsonString(PrettyPrint));
        }

        return true;
    }
}
