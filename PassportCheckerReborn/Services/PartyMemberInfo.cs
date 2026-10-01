namespace PassportCheckerReborn.Services;

public enum MemberNameState
{
    Resolved,

    // Waiting on an adventure plate lookup.
    Pending,

    // The lookup came back without a name.
    Unresolved,

    // The player's adventure plate is hidden.
    Private,
}

public record PartyMemberInfo(
    string Name,
    string World,
    string JobAbbreviation,
    ulong ContentId = 0,
    ushort WorldId = 0,
    MemberNameState NameState = MemberNameState.Resolved)
{
    public bool IsResolved => NameState == MemberNameState.Resolved;

    public bool IsPrivate => NameState == MemberNameState.Private;
}
