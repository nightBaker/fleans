namespace Fleans.Domain
{
    public record Workflow
    {
        private readonly WorkflowContext _context;

        public Workflow(Guid id,
                        int version,
                        List<IActivity> activities,
                        Dictionary<string, object> initialContext,
                        List<IWorkflowSequence> sequences)
        {
            Id = id;
            Version = version;
            Activities = activities;
            _context = new WorkflowContext(initialContext);
            Sequences = sequences;
        }

        public Guid Id { get; }
        public int Version { get; }
        public IReadOnlyCollection<IActivity> Activities { get; }
        public IReadOnlyCollection<IWorkflowSequence> Sequences { get; }
        public IReadOnlyDictionary<string, object> Context => _context.Context;
    }
}