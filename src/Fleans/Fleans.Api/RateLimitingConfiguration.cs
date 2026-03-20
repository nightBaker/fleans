namespace Fleans.Api;

public class RateLimitingConfiguration
{
    public RateLimitPolicy? WorkflowMutation { get; set; }
    public RateLimitPolicy? TaskOperation { get; set; }
    public RateLimitPolicy? Read { get; set; }
    public RateLimitPolicy? Admin { get; set; }
}

public class RateLimitPolicy
{
    public int Window { get; set; } = 60;
    public int PermitLimit { get; set; } = 100;
}
