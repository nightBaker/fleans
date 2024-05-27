using Fleans.Domain.Activities;

namespace Fleans.Domain.Sequences
{
    public class ConditionalSequenceFlow : SequenceFlow
    {
        public string Condition { get; }

        public ConditionalSequenceFlow(Activity source, Activity target, string condition)
            : base(source, target)
        {
            Condition = condition;
        }
    }
}
