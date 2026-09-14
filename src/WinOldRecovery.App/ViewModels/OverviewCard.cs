namespace WinOldRecovery.App.ViewModels;

public enum DecidePane
{
    Cards,
    Files,
}

public sealed record OverviewCard(
    string Title,
    string Summary,
    string Facts,
    string DecisionLabel,
    long? NodeId,
    string Kind,
    string DecisionTooltip = "")
{
    public bool ShowVerbs =>
        Kind is not ("Absent" or "Profile" or "HighValue" or "Regeneratable");
}
