using System.Collections.Concurrent;
using System.Dynamic;
using Fleans.Application.CustomTasks;
using Fleans.Worker.CustomTasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Streams;

namespace Fleans.Application.Tests.CustomTasks.TestStubs
{
    /// <summary>
    /// Two plugins meant to live on two different plugin hosts. Their handlers share a class name
    /// (in different namespaces), which by default would give them one grain type. Each records
    /// the silo it executed on, so a test can check neither ran on a silo that does not host it.
    /// </summary>
    public static class TestHostSliceExecutions
    {
        public static readonly ConcurrentQueue<(string TaskType, Guid ActivityInstanceId, string SiloName)> Executions = new();
    }
}

namespace Fleans.Application.Tests.CustomTasks.TestStubs.HostA
{
    [ImplicitStreamSubscription("events.ExecuteCustomTaskEvent.test-host-a")]
    public sealed partial class SliceCustomTaskHandler : CustomTaskHandlerBase
    {
        private readonly string _siloName;

        public SliceCustomTaskHandler(
            ILogger<SliceCustomTaskHandler> logger,
            IGrainFactory grainFactory,
            ILocalSiloDetails siloDetails) : base(logger, grainFactory)
        {
            _siloName = siloDetails.Name;
        }

        protected override string TaskType => "test-host-a";

        protected override Task<IDictionary<string, object?>> ExecuteAsync(
            IDictionary<string, object?> resolvedInputs,
            ExpandoObject variables,
            CustomTaskExecutionContext context,
            CancellationToken cancellationToken)
        {
            TestHostSliceExecutions.Executions.Enqueue((TaskType, context.ActivityInstanceId, _siloName));
            return Task.FromResult<IDictionary<string, object?>>(new Dictionary<string, object?>());
        }
    }
}

namespace Fleans.Application.Tests.CustomTasks.TestStubs.HostB
{
    [ImplicitStreamSubscription("events.ExecuteCustomTaskEvent.test-host-b")]
    public sealed partial class SliceCustomTaskHandler : CustomTaskHandlerBase
    {
        private readonly string _siloName;

        public SliceCustomTaskHandler(
            ILogger<SliceCustomTaskHandler> logger,
            IGrainFactory grainFactory,
            ILocalSiloDetails siloDetails) : base(logger, grainFactory)
        {
            _siloName = siloDetails.Name;
        }

        protected override string TaskType => "test-host-b";

        protected override Task<IDictionary<string, object?>> ExecuteAsync(
            IDictionary<string, object?> resolvedInputs,
            ExpandoObject variables,
            CustomTaskExecutionContext context,
            CancellationToken cancellationToken)
        {
            TestHostSliceExecutions.Executions.Enqueue((TaskType, context.ActivityInstanceId, _siloName));
            return Task.FromResult<IDictionary<string, object?>>(new Dictionary<string, object?>());
        }
    }
}
