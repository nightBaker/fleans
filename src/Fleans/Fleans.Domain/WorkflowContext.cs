﻿using Fleans.Domain.Exceptions;

namespace Fleans.Domain;

public class WorkflowContext : IContext
{        
    private readonly Dictionary<string, object> _context;
    private readonly Queue<IActivity> _nextActivities = new();   
    public WorkflowContext(Dictionary<string, object> context, IActivity firstActivity)
    {
        _context = context;        
        EnqueueNextActivity(firstActivity);
    }

    public IReadOnlyDictionary<string, object> Context => _context;
    public IActivity? CurrentActivity { get; private set; }

    public void EnqueueNextActivity(IActivity activity)
    {
        _nextActivities.Enqueue(activity);
    }

    public void EnqueueNextActivities( IEnumerable<IActivity> activities)
    {
        foreach(var activity in activities)
            _nextActivities.Enqueue(activity);
    }

    public bool GotoNextActivty()
    {
        if (_nextActivities.Count == 0)
        {
            return false;
        }
        
        // TODO do we need all acitivies completed before goint to next activity ?
        // if(CurrentActivity is not null && !CurrentActivity.IsCompleted)
        //     throw new CurrentActivityNotCompletedException("Complete current activity to continue execution.");
        
        CurrentActivity = _nextActivities.Dequeue();
        return true;
    }


}