namespace WinOldRecovery.Core.Decisions;

public enum Decision
{
    Undecided,
    Restore,
    LeaveBehind,
}

public enum DecisionSource
{
    Inherited,
    SuggestedDefault,
    User,
}

public sealed record DecisionAssignment(
    long NodeId,
    Decision Decision,
    DecisionSource Source,
    DateTimeOffset DecidedAt);

public sealed record SubtreeDecisionSummary(
    Decision Effective,
    bool Mixed,
    int RestoreCount,
    int LeaveBehindCount,
    int UndecidedCount);
