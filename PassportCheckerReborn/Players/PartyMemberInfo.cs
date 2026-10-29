namespace PassportCheckerReborn.Players;

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
