namespace Fleans.Domain.States;

[GenerateSerializer]
public class UserTaskMetadata
{
    [Id(0)] public string? Assignee { get; private set; }
    [Id(1)] public IReadOnlyList<string> CandidateGroups { get; private set; } = [];
    [Id(2)] public IReadOnlyList<string> CandidateUsers { get; private set; } = [];
    [Id(3)] public IReadOnlyList<string>? ExpectedOutputVariables { get; private set; }
    [Id(4)] public string? ClaimedBy { get; private set; }
    [Id(5)] public DateTimeOffset? ClaimedAt { get; private set; }
    [Id(6)] public UserTaskLifecycleState TaskState { get; private set; }

    public void Initialize(string? assignee, IReadOnlyList<string> candidateGroups,
        IReadOnlyList<string> candidateUsers, IReadOnlyList<string>? expectedOutputs)
    {
        Assignee = assignee;
        CandidateGroups = candidateGroups;
        CandidateUsers = candidateUsers;
        ExpectedOutputVariables = expectedOutputs;
        TaskState = UserTaskLifecycleState.Created;
    }

    public void Claim(string userId, DateTimeOffset claimedAt)
    {
        if (TaskState != UserTaskLifecycleState.Created)
            throw new InvalidOperationException("Task must be in Created state to claim");
        ClaimedBy = userId;
        ClaimedAt = claimedAt;
        TaskState = UserTaskLifecycleState.Claimed;
    }

    public void Unclaim()
    {
        if (TaskState != UserTaskLifecycleState.Claimed)
            throw new InvalidOperationException("Task must be in Claimed state to unclaim");
        ClaimedBy = null;
        ClaimedAt = null;
        TaskState = UserTaskLifecycleState.Created;
    }

    public void Complete()
    {
        if (TaskState != UserTaskLifecycleState.Claimed)
            throw new InvalidOperationException("Task must be in Claimed state to complete");
        TaskState = UserTaskLifecycleState.Completed;
    }
}

[GenerateSerializer]
public enum UserTaskLifecycleState
{
    Created,
    Claimed,
    Completed
}
