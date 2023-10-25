namespace Fleans.Domain
{
    public abstract record Activity<TResult>
    {
        public Guid Id { get; init; }
        public ActivityResult<TResult> Result { get; private set; }

        private List<ActivityResult<TResult>> _results = new List<ActivityResult<TResult>>();

        public IReadOnlyCollection<ActivityResult<TResult>> GetResults() => _results;

        protected void AddResult(ActivityResult<TResult> result)
        {
            Result = result;
            _results.Add(result);
        }
    }
}