using System.Text;
using System.Text.Json;
using WinOldRecovery.Core.Decisions;
using WinOldRecovery.Core.Recipes;

namespace WinOldRecovery.Recipes;

public sealed class VsCodeRecipe : IRecipe
{
    private static readonly (string IdSuffix, string Title, string UserRelative, string ExtensionsRelative)[] Products =
    [
        ("vscode", "VS Code", Path.Combine("AppData", "Roaming", "Code", "User"), Path.Combine(".vscode", "extensions")),
        ("vscodium", "VSCodium", Path.Combine("AppData", "Roaming", "VSCodium", "User"), Path.Combine(".vscode-oss", "extensions")),
        ("cursor", "Cursor", Path.Combine("AppData", "Roaming", "Cursor", "User"), Path.Combine(".cursor", "extensions")),
    ];

    public string Id => "vscode";

    public DetectResult Detect(ProfileContext context)
    {
        List<RecipeCard> cards = [];
        List<(string RelativePath, string Kind, string Detail)> badges = [];
        foreach ((string idSuffix, string title, string userRelative, string extensionsRelative) in Products)
        {
            string user = Path.Combine(context.OldProfileRoot, userRelative);
            if (!context.SafeFs.DirectoryExists(user))
            {
                continue;
            }

            List<string> files = [];
            foreach (string name in new[] { "settings.json", "keybindings.json", "tasks.json" })
            {
                if (context.SafeFs.FileExists(Path.Combine(user, name)))
                {
                    files.Add(name);
                }
            }

            bool snippets = context.SafeFs.DirectoryExists(Path.Combine(user, "snippets"));
            bool profiles = context.SafeFs.DirectoryExists(Path.Combine(user, "profiles"));
            if (files.Count == 0 && !snippets && !profiles)
            {
                continue;
            }

            string extensionsJson = Path.Combine(context.OldProfileRoot, extensionsRelative, "extensions.json");
            IReadOnlyList<string> extensionIds = context.SafeFs.FileExists(extensionsJson)
                ? ReadExtensionIds(context.SafeFs.ReadAllText(extensionsJson))
                : [];

            cards.Add(
                new RecipeCard(
                    Id,
                    title + " settings",
                    "Editor settings, keybindings, snippets, and an install-extensions command list.",
                    "These files are small and often unique after a reinstall.",
                    "User settings, keybindings, snippets, tasks, and profiles. globalStorage and workspaceStorage stay behind.",
                    "Settings Sync, if it was enabled.",
                    "workspaceStorage and globalStorage regenerate.",
                    "You keep a stock editor and lose custom keybindings and snippets.",
                    [
                        new RecipeComponent("settings", "Settings", string.Join(", ", files), Decision.Restore, false, null, false),
                        new RecipeComponent(
                            "state",
                            "State folders",
                            "globalStorage / workspaceStorage",
                            Decision.LeaveBehind,
                            true,
                            "State is regenerable and machine-bound.",
                            false),
                    ],
                    context.ProfileName + ":" + idSuffix,
                    new Dictionary<string, string>
                    {
                        ["user"] = user,
                        ["files"] = string.Join('|', files),
                        ["snippets"] = snippets ? "1" : "0",
                        ["profiles"] = profiles ? "1" : "0",
                        ["product"] = title,
                        ["cli"] = idSuffix == "cursor" ? "cursor" : idSuffix == "vscodium" ? "codium" : "code",
                        ["extensions"] = string.Join('|', extensionIds),
                    }));
            DetectorWalk.AddTreeBadge(badges, context.OldProfileRoot, user, title, "settings");
        }

        return new DetectResult(cards, badges);
    }

    public PlanResult Plan(CardDecisions decisions, DestinationContext destination)
    {
        if (!RecipeDecisions.ShouldRestore(decisions, "settings"))
        {
            return new PlanResult(decisions.Card, []);
        }

        string user = decisions.Card.Facts["user"];
        string destUser = Path.Combine(
            destination.DestinationProfileRoot,
            "AppData",
            "Roaming",
            ProductFolder(decisions.Card.Facts["product"]),
            "User");

        List<RecipeWrite> writes = [];
        foreach (string name in decisions.Card.Facts["files"].Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            DetectorWalk.CopyFileKeepBoth(
                destination.SafeFs,
                Path.Combine(user, name),
                Path.Combine(destUser, name),
                "settings",
                writes);
        }

        if (decisions.Card.Facts["snippets"] == "1")
        {
            AnkiRecipe.AddTree(
                destination.SafeFs,
                Path.Combine(user, "snippets"),
                Path.Combine(destUser, "snippets"),
                Path.Combine(user, "snippets"),
                "settings",
                writes,
                static _ => false);
        }

        if (decisions.Card.Facts["profiles"] == "1")
        {
            AnkiRecipe.AddTree(
                destination.SafeFs,
                Path.Combine(user, "profiles"),
                Path.Combine(destUser, "profiles"),
                Path.Combine(user, "profiles"),
                "settings",
                writes,
                static _ => false);
        }

        string cmd = BuildInstallScript(
            decisions.Card.Facts["cli"],
            decisions.Card.Facts["extensions"].Split('|', StringSplitOptions.RemoveEmptyEntries));
        writes.Add(
            new RecipeWrite(
                RecipeWriteKind.WriteContent,
                null,
                Path.Combine(destination.SessionExportsDirectory, "install-extensions-" + decisions.Card.Facts["cli"] + ".cmd"),
                cmd,
                cmd.Length,
                "settings"));
        return new PlanResult(decisions.Card, writes);
    }

    public Task ExecuteAsync(PlanResult plan, IRecipeJournal journal, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public RecipeVerifyResult Verify(PlanResult plan) =>
        DetectorWalk.FilesPresent(plan, "VS Code files present", "VS Code destination missing");

    public IReadOnlyList<Prerequisite> Prerequisites(PlanResult plan) =>
        [new Prerequisite("Code", "Close VS Code, VSCodium, or Cursor before restoring settings.")];

    internal static IReadOnlyList<string> ReadExtensionIds(string json)
    {
        List<string> ids = [];
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return ids;
            }

            foreach (JsonElement item in document.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("identifier", out JsonElement identifier) &&
                    identifier.TryGetProperty("id", out JsonElement id) &&
                    id.ValueKind == JsonValueKind.String)
                {
                    ids.Add(id.GetString()!);
                }
                else if (item.TryGetProperty("id", out JsonElement flat) &&
                    flat.ValueKind == JsonValueKind.String)
                {
                    ids.Add(flat.GetString()!);
                }
            }
        }
        catch (JsonException)
        {
        }

        return ids;
    }

    private static string ProductFolder(string product)
    {
        return product switch
        {
            "VSCodium" => "VSCodium",
            "Cursor" => "Cursor",
            _ => "Code",
        };
    }

    private static string BuildInstallScript(string cli, IReadOnlyList<string> ids)
    {
        StringBuilder builder = new();
        builder.AppendLine("@echo off");
        builder.AppendLine("REM Generated by WinOld Recovery. Review before running.");
        foreach (string id in ids)
        {
            builder.Append(cli);
            builder.Append(" --install-extension ");
            builder.AppendLine(id);
        }

        return builder.ToString();
    }
}
