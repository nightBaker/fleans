namespace Fleans.Api;

public class RateLimitingConfiguration
{
    public RateLimitPolicy WorkflowMutation { get; set; } = new() { Window = 60, PermitLimit = 100 };
    public RateLimitPolicy TaskOperation { get; set; } = new() { Window = 60, PermitLimit = 200 };
    public RateLimitPolicy Read { get; set; } = new() { Window = 60, PermitLimit = 300 };
    public RateLimitPolicy Admin { get; set; } = new() { Window = 60, PermitLimit = 20 };
}

public class RateLimitPolicy
{
    public int Window { get; set; } = 60;
    public int PermitLimit { get; set; } = 100;
}
