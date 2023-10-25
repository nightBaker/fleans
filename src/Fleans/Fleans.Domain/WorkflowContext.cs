namespace Fleans.Domain
{
    public class WorkflowContext : IContext
    {        
        private readonly Dictionary<string, object> _context;
        private readonly Dictionary<Guid, object> _internalContext;
        public WorkflowContext(Dictionary<string, object> context)
        {
            _context = context;
            _internalContext = new Dictionary<Guid, object>();
        }

        public IReadOnlyDictionary<string, object> Context => _context;

        public void AddActivityResult(Guid activityId, object value)
        {
            _internalContext.Add(activityId, value);
        }

        public void AddActivityResult(Guid activityId, bool value)
        {
            _internalContext.Add(activityId, value);
        }
    }
}