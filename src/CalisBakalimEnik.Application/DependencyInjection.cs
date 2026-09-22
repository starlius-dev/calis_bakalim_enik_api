using Microsoft.Extensions.DependencyInjection;

namespace CalisBakalimEnik.Application;

public static class DependencyInjection
{
    /// <summary>
    /// Nothing to register yet.
    /// </summary>
    /// <remarks>
    /// This used to call <c>AddValidatorsFromAssembly</c> against an assembly
    /// that contained no validators — a scan that could only ever find nothing,
    /// wired to a package nothing used. It read like validation was configured
    /// here, which is the opposite of true: validation lives in the endpoints,
    /// beside the rule, in the language the user reads it in. See
    /// docs/ARCHITECTURE.md §3.
    ///
    /// The hook stays because Program.cs calls it and this layer will
    /// eventually have something to register.
    /// </remarks>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        return services;
    }
}
