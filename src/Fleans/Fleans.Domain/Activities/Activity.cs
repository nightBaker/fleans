﻿namespace Fleans.Domain
{
    public abstract record Activity<TResult>
    {
        public Guid Id { get; }        
        public ActivityResult<TResult>? ExecutionResult { get; private set; }

        private readonly List<ActivityResult<TResult>> _results = new List<ActivityResult<TResult>>();
        public IReadOnlyCollection<ActivityResult<TResult>> GetResults() => _results;

        public bool IsCompleted => Status == ActivityStatus.Completed;

        public ActivityStatus Status { get; protected set; } = ActivityStatus.NotStarted;

        protected Activity(Guid id)
        {            
            Id = id;            
        }        
    }
}