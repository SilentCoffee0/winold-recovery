using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinOldRecovery.Core.Decisions;

namespace WinOldRecovery.Core.Classification;

public static class ClassificationRuleCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly Lazy<IReadOnlyList<ClassificationRule>> EmbeddedRules = new(LoadAll, isThreadSafe: true);

    public static IReadOnlyList<ClassificationRule> LoadEmbedded() => EmbeddedRules.Value;

    private static IReadOnlyList<ClassificationRule> LoadAll()
    {
        Assembly assembly = typeof(ClassificationRuleCatalog).Assembly;
        string[] names =
        [
            "WinOldRecovery.Core.Classification.Rules.HighValue.rules.json",
            "WinOldRecovery.Core.Classification.Rules.Regeneratable.rules.json",
            "WinOldRecovery.Core.Classification.Rules.GameSaves.rules.json",
            "WinOldRecovery.Core.Classification.Rules.Sensitive.rules.json",
        ];

        List<ClassificationRule> rules = [];
        foreach (string name in names)
        {
            using Stream? stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                throw new InvalidOperationException("Missing embedded classification rules: " + name);
            }

            ClassificationRuleFile file = JsonSerializer.Deserialize<ClassificationRuleFile>(stream, JsonOptions)
                ?? new ClassificationRuleFile();
            foreach (ClassificationRule rule in file.Rules)
            {
                if (rule.Kind == ClassificationKind.Regeneratable &&
                    rule.SuggestedDefault == Decision.LeaveBehind)
                {
                    throw new InvalidOperationException(
                        "Regeneratable rule '" + rule.Id + "' cannot suggest Leave Behind.");
                }

                rules.Add(rule);
            }
        }

        return rules;
    }
}
