using System.Text.Json;
using Launchpad.Core.Apps;

static class Classify
{
    private record Cached(string Id, string Name, string Target);

    public static void Run()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Launchpad", "apps.json");
        var apps = JsonSerializer.Deserialize<List<Cached>>(File.ReadAllText(path))!;
        bool Util(Cached a) => AppClassifier.IsUtility(a.Name, a.Target) || (Path.IsPathRooted(a.Target) && Directory.Exists(a.Target));
        var hidden = apps.Where(Util).ToList();
        var kept = apps.Where(a => !Util(a)).ToList();
        Console.WriteLine($"{apps.Count} apps: {kept.Count} kept, {hidden.Count} hidden as utilities\n\n--- HIDDEN ---");
        foreach (var a in hidden) Console.WriteLine($"  {a.Name}");
        Console.WriteLine("\n--- KEPT ---");
        Console.WriteLine(string.Join(" | ", kept.Select(a => a.Name)));
    }
}
