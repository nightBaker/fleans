using AWS.MSK.Auth;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;

namespace Fleans.Streaming.Kafka;

internal static partial class MskIamTokenRefresher
{
    // async void is deliberate: Confluent.Kafka's refresh callback is fire-and-forget.
    // The only signal back to the client on failure is OAuthBearerSetTokenFailure.
    public static async void Refresh(IClient client, string region, ILogger logger)
    {
        try
        {
            var (token, expiryMs) = await new AWSMSKAuthTokenGenerator()
                .GenerateAuthTokenAsync(Amazon.RegionEndpoint.GetBySystemName(region));
            client.OAuthBearerSetToken(token, expiryMs, "fleans-msk-iam");
        }
        catch (Exception ex)
        {
            LogTokenRefreshFailed(logger, region, ex);
            try
            {
                client.OAuthBearerSetTokenFailure(ex.Message);
            }
            catch (Exception inner)
            {
                // OAuthBearerSetTokenFailure can throw on a disposed IClient.
                // An unhandled throw inside async void crashes the silo — swallow here.
                LogSetTokenFailureThrew(logger, inner);
            }
        }
    }

    [LoggerMessage(EventId = 11200, Level = LogLevel.Error,
        Message = "MSK IAM token refresh failed for region {Region}")]
    private static partial void LogTokenRefreshFailed(ILogger logger, string Region, Exception exception);

    [LoggerMessage(EventId = 11201, Level = LogLevel.Warning,
        Message = "OAuthBearerSetTokenFailure threw after a token-refresh failure — client may be disposed")]
    private static partial void LogSetTokenFailureThrew(ILogger logger, Exception exception);
}
