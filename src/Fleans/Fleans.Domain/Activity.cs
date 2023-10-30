namespace Fleans.Domain
{
    public abstract record Activity<TResult>
    {
        public Guid Id { get; }
        public IWorkflowConnection[] Connections { get; }
        public ActivityResult<TResult>? ExecutionResult { get; private set; }

        private readonly List<ActivityResult<TResult>> _results = new List<ActivityResult<TResult>>();
        public IReadOnlyCollection<ActivityResult<TResult>> GetResults() => _results;

        public bool IsCompleted => ExecutionResult is not null;
        
        protected Activity(Guid id, IWorkflowConnection[] connections)
        {            
            Id = id;
            Connections = connections;
        }

        protected void AddResult(ActivityResult<TResult> result)
        {
            ExecutionResult = result;
            _results.Add(result);
        }
    }
}