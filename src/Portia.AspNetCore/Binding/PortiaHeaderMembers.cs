namespace Cntryl.Portia;

sealed record PortiaHeaderMembers(IReadOnlyList<PortiaHeaderMember> Members);

sealed record PortiaHeaderMember(string MemberName, string Name);
