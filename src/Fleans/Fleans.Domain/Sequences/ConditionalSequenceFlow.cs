using Fleans.Domain.Activities;

namespace Fleans.Domain.Sequences
{
    public class ConditionalSequenceFlow : SequenceFlow
    {
        public string Condition { get; }

        public ConditionalSequenceFlow(string sequencId, Activity source, Activity target, string condition)
            : base(sequencId, source: source, target: target)
        {
            Condition = condition;
        }
    }
}
