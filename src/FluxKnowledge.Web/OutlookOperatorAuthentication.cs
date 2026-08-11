using Microsoft.AspNetCore.Authentication;

namespace FluxKnowledge.Web;

public static class OutlookOperatorAuthentication
{
    public static void UseOutlookOperatorAuthentication(this WebApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);

        // WebApplication otherwise inserts authentication globally when a scheme
        // is registered. Keep existing read-only endpoints public and challenge
        // only the local Outlook operator route.
        ((IApplicationBuilder)application).Properties["__AuthenticationMiddlewareSet"] = true;
        ((IApplicationBuilder)application).Properties["__AuthorizationMiddlewareSet"] = true;
        application.UseWhen(
            context => context.Request.Path.StartsWithSegments("/outlook") ||
                context.Request.Path.StartsWithSegments("/_blazor"),
            branch =>
            {
                branch.UseAuthentication();
                branch.UseAuthorization();
                branch.Use(async (context, next) =>
                {
                    if (context.Request.Path.StartsWithSegments("/outlook") &&
                        context.User.Identity?.IsAuthenticated != true)
                    {
                        await context.ChallengeAsync().ConfigureAwait(false);
                        return;
                    }

                    await next(context).ConfigureAwait(false);
                });
            });
    }
}
