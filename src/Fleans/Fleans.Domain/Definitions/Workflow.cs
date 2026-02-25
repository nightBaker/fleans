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

        /// <summary>
        /// Finds the matching BoundaryErrorEvent for a failed activity, searching the activity's
        /// scope and walking up parent SubProcess scopes if not found.
        /// Specific error code matches take priority over catch-all (null ErrorCode) boundaries.
        /// </summary>
        (BoundaryErrorEvent BoundaryEvent, IWorkflowDefinition Scope, string AttachedToActivityId)?
            FindBoundaryErrorHandler(string failedActivityId, string? errorCode)
        {
            var targetActivityId = failedActivityId;

            while (true)
            {
                var scope = FindScopeForActivity(targetActivityId);
                if (scope is null) return null;

                var candidates = scope.Activities
                    .OfType<BoundaryErrorEvent>()
                    .Where(b => b.AttachedToActivityId == targetActivityId
                        && (b.ErrorCode == null || b.ErrorCode == errorCode))
                    .ToList();

                // Prefer specific error code match over catch-all
                var match = candidates.FirstOrDefault(b => b.ErrorCode == errorCode)
                            ?? candidates.FirstOrDefault(b => b.ErrorCode == null);

                if (match is not null)
                    return (match, scope, targetActivityId);

                // Bubble up: if scope is a SubProcess, check its parent for boundary on the SubProcess
                if (scope is SubProcess subProcess)
                    targetActivityId = subProcess.ActivityId;
                else
                    return null; // at root scope, no match found
            }
        }

        /// <summary>
        /// Returns activity IDs of sibling catch events that compete with the given activity
        /// after an EventBasedGateway. Returns empty set if the activity is not downstream
        /// of an EventBasedGateway.
        /// </summary>
        IReadOnlySet<string> GetEventBasedGatewaySiblings(string completedActivityId)
        {
            var scope = FindScopeForActivity(completedActivityId);
            if (scope is null) return new HashSet<string>();

            var gatewayFlow = scope.SequenceFlows
                .Where(sf => sf.Target.ActivityId == completedActivityId)
                .FirstOrDefault(sf => sf.Source is EventBasedGateway);

            if (gatewayFlow?.Source is not EventBasedGateway gateway)
                return new HashSet<string>();

            return scope.SequenceFlows
                .Where(sf => sf.Source == gateway && sf.Target.ActivityId != completedActivityId)
                .Select(sf => sf.Target.ActivityId)
                .ToHashSet();
        }
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
