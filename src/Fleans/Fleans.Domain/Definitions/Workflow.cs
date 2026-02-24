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
        bool IsRootScope { get; }
        Activity GetActivity(string activityId);

        /// <summary>
        /// Recursively searches this definition and nested SubProcess scopes
        /// for the scope that directly contains the given activity.
        /// Returns null if not found.
        /// </summary>
        IWorkflowDefinition? FindScopeForActivity(string activityId)
        {
            if (Activities.Any(a => a.ActivityId == activityId))
                return this;

            foreach (var subProcess in Activities.OfType<SubProcess>())
            {
                var result = ((IWorkflowDefinition)subProcess).FindScopeForActivity(activityId);
                if (result is not null)
                    return result;
            }

            return null;
        }

        /// <summary>
        /// Same as <see cref="FindScopeForActivity"/> but throws if the activity is not found.
        /// </summary>
        IWorkflowDefinition GetScopeForActivity(string activityId)
            => FindScopeForActivity(activityId)
                ?? throw new InvalidOperationException($"Activity '{activityId}' not found in any definition scope");

        /// <summary>
        /// Finds the activity by searching all scopes recursively.
        /// </summary>
        Activity GetActivityAcrossScopes(string activityId)
            => GetScopeForActivity(activityId).GetActivity(activityId);
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

        public bool IsRootScope => true;

        public Activity GetActivity(string activityId)
            => Activities.First(a => a.ActivityId == activityId);
    }
}
