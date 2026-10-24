using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Fleans.Api.Admission;

/// <summary>
/// Wires <see cref="AdaptiveConcurrencyLimiter"/> in front of the REST mutation endpoints — those
/// carrying the <c>workflow-mutation</c> or <c>task-operation</c> rate-limiting policy, i.e. the
/// ones that call workflow grains. Reads and admin endpoints are never limited by it. The fixed
/// <c>RateLimiting:*</c> policies keep working and run first: a request they reject never takes an
/// admission slot.
/// </summary>
public static class AdmissionExtensions
{
    private static readonly HashSet<string> AdmittedPolicies = ["workflow-mutation", "task-operation"];

    /// <summary>Binds <c>Fleans:Admission</c> and registers the limiter.</summary>
    public static IServiceCollection AddFleansAdmission(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AdmissionOptions>().Bind(configuration.GetSection(AdmissionOptions.SectionName));
        services.AddSingleton(sp => new AdaptiveConcurrencyLimiter(
            sp.GetRequiredService<IOptions<AdmissionOptions>>().Value,
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            sp.GetRequiredService<ILogger<AdaptiveConcurrencyLimiter>>()));
        return services;
    }

    /// <summary>
    /// Adds the admission middleware unless <c>Fleans:Admission:Enabled</c> is <c>false</c>. Fails
    /// fast (at pipeline build, before the host starts) on invalid options.
    /// </summary>
    public static IApplicationBuilder UseFleansAdmission(this IApplicationBuilder app)
    {
        var options = app.ApplicationServices.GetRequiredService<IOptions<AdmissionOptions>>().Value;
        if (!options.Enabled) return app;
        if (options.Validate() is { } error) throw new InvalidOperationException(error);

        var limiter = app.ApplicationServices.GetRequiredService<AdaptiveConcurrencyLimiter>();
        var time = app.ApplicationServices.GetService<TimeProvider>() ?? TimeProvider.System;
        return app.Use(async (context, next) =>
        {
            var policy = context.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;
            if (policy is null || !AdmittedPolicies.Contains(policy))
            {
                await next(context);
                return;
            }

            if (!limiter.TryAcquire())
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.Response.Headers.RetryAfter = "1";
                await context.Response.WriteAsJsonAsync(
                    new { error = "The cluster is at its admission limit. Retry later." }, context.RequestAborted);
                return;
            }

            var started = time.GetTimestamp();
            var overloaded = false;
            try
            {
                await next(context);
            }
            catch (Exception ex) when (IsOverload(ex))
            {
                overloaded = true;
                throw;
            }
            finally
            {
                limiter.Release(time.GetElapsedTime(started), overloaded);
            }
        });
    }

    /// <summary>
    /// Failures that say the cluster is overloaded rather than that the request was wrong: a grain
    /// call timeout, or any Orleans runtime failure — <c>GatewayTooBusyException</c> from a
    /// shedding gateway among them.
    /// </summary>
    private static bool IsOverload(Exception ex) => ex is TimeoutException or Orleans.Runtime.OrleansException;
}
