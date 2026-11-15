using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

// A declared [RequiresPermission] is an authorizer like any other: it occupies the Permission stage
// of the one ordered list dispatch runs, instead of being a separate step between two lists.
sealed class PermissionAuthorizerRegistration(Type requestType, Func<IRequestBase, string> permission)
    : RequestAuthorizerRegistration(requestType, typeof(IPermissionEvaluator), AuthorizationStage.Permission)
{
    internal override string ComponentName => "permission";

    internal override async ValueTask<Result> AuthorizeAsync(IServiceProvider services, IRequestBase request,
        IRequestContext context, CancellationToken ct)
    {
        var evaluator = services.GetRequiredService<IPermissionEvaluator>();
        var result = await evaluator.EvaluateAsync(context.Actor, permission(request), ct).ConfigureAwait(false);
        // The registration only knows the contract; the application's evaluator is the type to name.
        RequestResults.Validate(result, evaluator.GetType(), "permission evaluator");
        return result;
    }

    // The application registers its own evaluator; hosted startup verifies one exists.
    internal override void Register(IServiceCollection services)
    {
    }
}
