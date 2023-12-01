using Fleans.Domain.Exceptions;
using System.Collections.Generic;
using System.Linq;
using Fleans.Domain.Activities;
using Fleans.Domain.Connections;

namespace Fleans.Domain;

public partial class Workflow
{
    private readonly IContext _context;
    private readonly WorkflowDefinition _definition;
    
    public Workflow(Guid id,
                    Dictionary<string, object> initialContext,
                    WorkflowDefinition definition)
    {
        Id = id;
        _context = new WorkflowContext(initialContext);
        _definition = definition;
        
    }

    public Guid Id { get; }
    public WorkflowStatus Status { get; private set; }

    public async Task Start()
    {
        var firstStartEvent = _definition.StartEvents.First(x => x.IsDefault);
        await _startProcess(firstStartEvent);
    }
    
    public async Task Message(IMessage message)
    {
        if (Status == WorkflowStatus.Waiting)
        {
            //TODO complete message event 
        }
        else
        {
            //try to start process

            var firstStartEvent = _definition.StartEvents.First(x => x.CorrelationKey == message.CorrelationKey);

            await _startProcess(firstStartEvent);
        }
    }

    private async Task _startProcess(IStartProcessEventActivity startEvent)
    {
        var connection = _definition.Connections[startEvent.Id].First();

        _context.EnqueueNextActivities(new[] { connection.To });

        await Run();
    }

    public async Task Run()
    {
        Status = WorkflowStatus.Running;

        while (_context.GotoNextActivty())
        {
            var activity = _context.CurrentActivity!;

            if (activity is IExecutableActivity executableActivity)
            {

                try
                {
                    var result = await executableActivity.ExecuteAsync(_context);

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
                    executableActivity.Fail(e);

                    if (_definition.Connections.TryGetValue(activity.Id, out var allConnections))
                    {
                        var nextActivities = allConnections.OfType<IWorkflowErrorConecction>()
                            .Where(x => x.CanExecute(_context, e)).Select(x => x.To);
                        _context.EnqueueNextActivities(nextActivities);
                    }
                }
            }
        }

        Status = _context.CurrentActivity switch
        {
            null => WorkflowStatus.Completed,
            IExecutableActivity { Status: ActivityStatus.Completed } => WorkflowStatus.Completed,
            IExecutableActivity { Status: ActivityStatus.Failed } => WorkflowStatus.Failed,
            IExecutableActivity { Status: ActivityStatus.Waiting } => WorkflowStatus.Waiting,
            _ => throw new NotSupportedActivityStatusException()
        };

    }
}