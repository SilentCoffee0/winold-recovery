namespace WinOldRecovery.Core.Decisions;

public static class DecisionDisplay
{
    public const string SuggestedTooltip = "Suggested default. Confirm or change it.";
    public const string InheritedTooltip = "Inherited from a parent folder.";

    public static string Chip(Decision decision, bool suggestedUnconfirmed)
    {
        if (decision == Decision.Undecided)
        {
            return "○";
        }

        if (suggestedUnconfirmed || decision == Decision.LeaveBehind)
        {
            return "◌";
        }

        return "●";
    }

    public static string Label(Decision decision, bool ownUser, bool suggested)
    {
        string chip = Chip(decision, suggestedUnconfirmed: suggested && !ownUser);
        string name = decision == Decision.LeaveBehind ? "Leave Behind" : decision.ToString();
        if (ownUser)
        {
            return chip + " " + name;
        }

        if (decision == Decision.Undecided)
        {
            return chip + " Undecided";
        }

        if (suggested)
        {
            return chip + " " + name + " (suggested)";
        }

        return chip + " " + name + " (inherited)";
    }

    public static string? Tooltip(bool ownUser, bool suggested, bool inherited)
    {
        if (suggested && !ownUser)
        {
            return SuggestedTooltip;
        }

        if (inherited)
        {
            return InheritedTooltip;
        }

        return null;
    }
}
