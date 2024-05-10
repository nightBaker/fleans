using Fleans.Domain.Activities;

namespace Fleans.Domain.Sequences
{
    public class SequenceFlow
    {
        public Activity Source { get; }
        public Activity Target { get; }

        public SequenceFlow(Activity source, Activity target)
        {
            Source = source;
            Target = target;
        }
    }
}
