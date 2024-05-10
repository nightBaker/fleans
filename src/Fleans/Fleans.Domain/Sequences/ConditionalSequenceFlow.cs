using Fleans.Domain.Activities;

namespace Fleans.Domain.Sequences
{
    public class ConditionalSequenceFlow : SequenceFlow
    {
        public ICondition Condition { get; }

        public ConditionalSequenceFlow(Activity source, Activity target, ICondition condition)
            : base(source, target)
        {
            Condition = condition;
        }
    }
}
