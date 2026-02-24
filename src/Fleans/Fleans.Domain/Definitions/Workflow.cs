using System;
using System.Collections.Generic;

using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;

namespace Fleans.Domain
{
    public interface IWorkflowDefinition
    {
        string WorkflowId { get; }
        string? ProcessDefinitionId { get; }
        List<Activity> Activities { get; }
        List<SequenceFlow> SequenceFlows { get; }
        List<MessageDefinition> Messages { get; }
        List<SignalDefinition> Signals { get; }
        Activity GetActivity(string activityId);

        /// <summary>
        /// True for the root process definition; false for embedded sub-process scopes.
        /// Used by EndEvent to decide whether to complete the whole workflow instance.
        /// </summary>
        bool IsRootProcess => true;
    }
   
    [GenerateSerializer]
    public record WorkflowDefinition : IWorkflowDefinition
    {
        [Id(0)]
        public required string WorkflowId { get; init; }

        [Id(1)]
        public required List<Activity> Activities { get; init; }

        [Id(2)]
        public required List<SequenceFlow> SequenceFlows { get; init; }

        [Id(3)]
        public string? ProcessDefinitionId { get; init; }

        [Id(4)]
        public List<MessageDefinition> Messages { get; init; } = [];

        [Id(5)]
        public List<SignalDefinition> Signals { get; init; } = [];

        public Activity GetActivity(string activityId)
            => Activities.First(a => a.ActivityId == activityId);
    }
}
