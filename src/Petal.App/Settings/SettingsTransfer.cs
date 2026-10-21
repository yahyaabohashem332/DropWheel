using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Petal.App.Settings;

/// <summary>What an exported settings file holds, for the question before it is imported.</summary>
public sealed record SettingsSummary(int Wheels, int Favourites, int Recipes, int Automations, int Petals, DateTime Exported);

/// <summary>
/// Petal's settings as one file to keep or carry to another PC: everything in Settings, the wheels, and the
/// user's own petals. Which petals and scripts were allowed to run is left out, so on the other PC each one
/// is shown once before it runs.
/// </summary>
public static class SettingsTransfer
{
    public const string Extension = "petal";
    private const string Format = "petal-settings";

    public static void Export(AppSettings settings, string path)
    {
        var json = JsonSerializer.SerializeToNode(settings, SettingsStore.Options)!.AsObject();
        json.Remove(nameof(AppSettings.ApprovedPetals));
        json.Remove(nameof(AppSettings.LastUpdateCheck));
        json.Remove(nameof(AppSettings.SkippedVersion));
        var petals = new JsonArray();
        if (Directory.Exists(Petal.Recipes.CustomPetal.Folder))
        {
            foreach (var file in Directory.EnumerateFiles(Petal.Recipes.CustomPetal.Folder, "*.json").Order(StringComparer.OrdinalIgnoreCase))
                petals.Add(new JsonObject { ["file"] = Path.GetFileName(file), ["content"] = File.ReadAllText(file) });
        }
        var root = new JsonObject
        {
            ["format"] = Format,
            ["version"] = 1,
            ["exported"] = DateTime.UtcNow,
            ["settings"] = json,
            ["petals"] = petals,
        };
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Reads an exported file: its settings, its petals and a summary; throws <see cref="InvalidDataException"/> for anything else.</summary>
    public static (AppSettings Settings, IReadOnlyList<(string File, string Content)> Petals, SettingsSummary Summary) Read(string path)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new InvalidDataException();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new InvalidDataException("This isn't a Petal settings file.", ex);
        }
        if (root["format"]?.GetValue<string>() != Format || root["settings"] is not JsonObject json)
            throw new InvalidDataException("This isn't a Petal settings file.");
        AppSettings settings;
        try
        {
            settings = json.Deserialize<AppSettings>(SettingsStore.Options) ?? throw new InvalidDataException("This isn't a Petal settings file.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("This settings file is damaged.", ex);
        }
        var petals = new List<(string, string)>();
        if (root["petals"] is JsonArray list)
        {
            foreach (var node in list.OfType<JsonObject>())
            {
                var name = Path.GetFileName(node["file"]?.GetValue<string>() ?? "");
                var content = node["content"]?.GetValue<string>();
                if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && content is not null && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
                    petals.Add((name, content));
            }
        }
        var exported = root["exported"]?.GetValue<DateTime>() ?? File.GetLastWriteTimeUtc(path);
        return (settings, petals, new SettingsSummary(settings.Wheels.Count, settings.Favourites.Count, settings.Recipes.Count, settings.Automations.Count, petals.Count, exported));
    }

    /// <summary>
    /// Puts imported settings in place: this PC's allowed petals and scripts are kept (and nothing new is
    /// allowed), the petals are written to the petals folder, and everything is applied at once.
    /// </summary>
    public static void Apply(AppSettings imported, IReadOnlyList<(string File, string Content)> petals)
    {
        imported.ApprovedPetals = [.. SettingsStore.Current.ApprovedPetals];
        imported.LastUpdateCheck = SettingsStore.Current.LastUpdateCheck;
        if (petals.Count > 0)
        {
            Directory.CreateDirectory(Petal.Recipes.CustomPetal.Folder);
            foreach (var (file, content) in petals)
                File.WriteAllText(Path.Combine(Petal.Recipes.CustomPetal.Folder, file), content);
        }
        SettingsStore.Replace(imported);
        SettingsStore.Save();
    }
}
