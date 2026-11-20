using System.Linq.Expressions;
using System.Reflection;

namespace Cntryl.Portia;

static class PortiaHttpMemberSelector
{
    // The selector is inspected, never compiled, so it stays trimming- and Native AOT-safe.
    internal static string Name(LambdaExpression member, string failure)
    {
        ArgumentNullException.ThrowIfNull(member);
        var body = member.Body is UnaryExpression { NodeType: ExpressionType.Convert } convert
            ? convert.Operand
            : member.Body;
        return body is MemberExpression { Member: PropertyInfo or FieldInfo } access &&
               access.Expression == member.Parameters[0]
            ? access.Member.Name
            : throw new ArgumentException(failure, nameof(member));
    }
}
