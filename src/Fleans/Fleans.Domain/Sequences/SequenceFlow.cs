using Fleans.Domain.Activities;

namespace Fleans.Domain.Sequences
{
    public class SequenceFlow
    {
        public string SequenceFlowId { get; }
        public Activity Source { get; }
        public Activity Target { get; }

        public SequenceFlow(string sequenceFlowId, Activity source, Activity target)
        {
            Source = source;
            Target = target;
            SequenceFlowId = sequenceFlowId;
        }
    }
}
