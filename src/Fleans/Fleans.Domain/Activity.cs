namespace Fleans.Domain
{
    public abstract record Activity<TResult>
    {
        public Guid Id { get; }
        public IWorkflowConnection[] Connections { get; }
        public ActivityResult<TResult>? Result { get; private set; }

        private readonly List<ActivityResult<TResult>> _results = new List<ActivityResult<TResult>>();
        public IReadOnlyCollection<ActivityResult<TResult>> GetResults() => _results;

        protected bool IsCompleted => Result != null;
        
        protected Activity(Guid id, IWorkflowConnection[] connections)
        {            
            Id = id;
            Connections = connections;
        }

        protected void AddResult(ActivityResult<TResult> result)
        {
            Result = result;
            _results.Add(result);
        }
    }
}