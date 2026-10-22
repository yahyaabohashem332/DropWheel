using Petal.App.Localization;
using Petal.App.Settings;
using Petal.Core.Text;

namespace Petal.App.Commands;

/// <summary>What the features of their own put in the command palette: snippets to type, workspaces to open.</summary>
internal static class PaletteSources
{
    public static void Register()
    {
        CommandPalette.Sources.Add(Snippets);
        CommandPalette.Sources.Add(Workspaces);
    }

    private static IEnumerable<PaletteItem> Snippets(string query, PaletteContext context)
    {
        foreach (var snippet in SettingsStore.Current.Snippets)
        {
            int score = Fuzzy.Best(query, snippet.Abbreviation, snippet.Text, "snippet " + snippet.Abbreviation, "مقتطف " + snippet.Abbreviation);
            if (score <= 0)
                continue;
            var item = snippet;
            var preview = item.Text.Replace("\r", " ").Replace("\n", " ");
            yield return new PaletteItem("snippet:" + item.Id, L.F("Type {0}", item.Abbreviation), preview.Length > 80 ? preview[..80] + "…" : preview, "type",
                c => Words.TextExpander.TypeAsync(item, c.Target))
            { Kind = L.T("Snippet"), Score = score };
        }
    }

    private static IEnumerable<PaletteItem> Workspaces(string query, PaletteContext context)
    {
        foreach (var workspace in SettingsStore.Current.Workspaces)
        {
            int score = Fuzzy.Best(query, workspace.Name, "workspace " + workspace.Name, "مساحة " + workspace.Name);
            if (score <= 0)
                continue;
            var item = workspace;
            yield return new PaletteItem("workspace:" + item.Id, L.F("Open the {0} workspace", item.Name), L.P(item.Windows.Count, "{0} window", "{0} windows"), "layoutGrid",
                _ => Screen.Workspaces.OpenAsync(item))
            { Kind = L.T("Workspace"), Score = score };
        }
    }
}
