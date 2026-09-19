namespace CalisBakalimEnik.Application.Common.Interfaces;

/// <summary>
/// The authenticated user for this request, resolved from the validated JWT's
/// <c>sub</c> claim — never from a header or body. See docs/SECURITY.md §2.
/// </summary>
public interface ICurrentUser
{
    Guid? Id { get; }
    bool IsAuthenticated { get; }
    IReadOnlyCollection<string> Permissions { get; }
    bool HasPermission(string permission);
}
