﻿using Fleans.Domain.Exceptions;
using System.Collections.Generic;
using System.Linq;

namespace Fleans.Domain;

public partial class Workflow
{
    private readonly IContext _context;
    private readonly WorkflowDefinition _definition;

    public Workflow(Guid id,
                    Dictionary<string, object> initialContext,
                    IActivity firstActivity,
                    WorkflowDefinition definition)
    {
        Id = id;
        _context = new WorkflowContext(initialContext, firstActivity);
        _definition = definition;
    }

    public Guid Id { get; }
    public WorkflowStatus Status { get; private set; }

    public async Task Run()
    {
        Status = WorkflowStatus.Running;

        while (_context.GotoNextActivty())
        {
            var activity = _context.CurrentActivity!;

            try
            {
                var result = await activity.ExecuteAsync(_context);

                if (result.ActivityResultStatus == ActivityResultStatus.Failed
                    && result.ActivityResultStatus == ActivityResultStatus.Waiting)
                {
                    continue;
                }

                if (_definition.Connections.TryGetValue(activity.Id, out var connections))
                {
                    var nextActivities = connections.Where(x => x.CanExecute(_context)).Select(x => x.To);
                    _context.EnqueueNextActivities(nextActivities);
                }
            }
            catch (Exception e)
            {
                activity.Fail(e);

                if (_definition.Connections.TryGetValue(activity.Id, out var allConnections))
                {
                    var nextActivities = allConnections.OfType<IWorkflowErrorConecction>()
                                                        .Where(x => x.CanExecute(_context, e)).Select(x => x.To);
                    _context.EnqueueNextActivities(nextActivities);
                }
            }
        }

        Status = _context.CurrentActivity switch
        {
            null => WorkflowStatus.Completed,
            not null when _context.CurrentActivity.Status == ActivityStatus.Completed => WorkflowStatus.Completed,

            not null when _context.CurrentActivity.Status == ActivityStatus.Failed => WorkflowStatus.Failed,
            not null when _context.CurrentActivity.Status == ActivityStatus.Waiting => WorkflowStatus.Waiting,
            _ => throw new NotSupportedActivityStatusException()
        };

    }
}
