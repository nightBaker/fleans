namespace Fleans.Application.CustomTasks;

public sealed record CustomTaskRegistration(string TaskType, Type GrainInterface);
