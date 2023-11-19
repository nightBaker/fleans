namespace Fleans.Domain
{
    public abstract record Activity<TResult>
    {
        public Guid Id { get; }        
        public ActivityResult<TResult>? ExecutionResult { get; protected set; }

        private readonly List<ActivityResult<TResult>> _results = new List<ActivityResult<TResult>>();
        public IReadOnlyCollection<ActivityResult<TResult>> GetResults() => _results;

        public bool IsCompleted => Status == ActivityStatus.Completed;

        public ActivityStatus Status { get; protected set; } = ActivityStatus.NotStarted;

        public void Fail(Exception exception)
        {
            Status = ActivityStatus.Failed;
            ExecutionResult = new ErrorActivityResult<TResult>(exception);
            _results.Add(ExecutionResult);
        }

        protected Activity(Guid id)
        {            
            Id = id;            
        }        
    }
}