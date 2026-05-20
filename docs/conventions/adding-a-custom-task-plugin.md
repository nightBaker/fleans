# Adding a custom-task plugin

A *custom task* is a `<bpmn:serviceTask type="…">` whose execution is supplied by a user-written plugin grain on a Worker silo. Use this — **not** [Adding a new BPMN activity](./adding-a-bpmn-activity.md) — when the new behavior is plugin-shaped (REST call, email, custom external system).

## Steps

1. **Project setup.** Add a new project (e.g. `Fleans.Plugins.MyThing`) referencing `Fleans.Worker`. Inside it, write a class deriving from `Fleans.Worker.CustomTasks.CustomTaskHandlerBase`. Override `TaskType` and `ExecuteAsync(...)`. The subclass MUST carry `[ImplicitStreamSubscription("events.ExecuteCustomTaskEvent.<your-task-type>")]` as a literal string (attribute arguments must be compile-time constants); `CustomTaskHandlerBase` no longer carries a class-level subscription attribute. See [plugin-stack.md](./plugin-stack.md) and [streaming.md](./streaming.md) for the reasoning.

2. **Error contract.** Throw `Fleans.Domain.Errors.CustomTaskFailedActivityException(int code, string message)` from `ExecuteAsync` to fail with a typed error; any other thrown exception fails with code 500.

   **Cancellation is not a failure.** `OperationCanceledException` thrown when the supplied `CancellationToken` is signalled is treated as a redelivery hint, NOT a failure — plugin authors should `await` against the token (`HttpClient.SendAsync(request, ct)`, `Task.Delay(timeout, ct)`, etc.) so the grain can deactivate cleanly during silo shutdown. The base class re-throws (no `FailActivity` call); the stream provider redelivers the event after grain reactivation. Plugin-internal cancellation (an OCE whose source is the plugin's own CTS, NOT the supplied grain-lifetime token) still routes to `FailActivity` — the `when (_grainLifetimeCts?.IsCancellationRequested == true)` filter in `CustomTaskHandlerBase.OnNextAsync` distinguishes the two cases.

3. **Return shape.** `ExecuteAsync` returns an `IDictionary<string, object?>`. Output mappings (`<zeebe:output source="=__response.body" target="…"/>`) walk that dictionary.

4. **DI registration.** Expose an extension method on the plugin assembly:

   ```csharp
   public static IServiceCollection AddMyThingPlugin(this IServiceCollection services) =>
       services.AddCustomTaskPlugin<MyThingHandler>(taskType: "my-thing", displayName: "My Thing");
   ```

   Plugin authors who want their plugin to live in the catalog UI must call this from the Worker silo's host registration.

   `AddCustomTaskPlugin<T>` validates two contracts at silo startup and throws `InvalidOperationException` on either failure:
   - (a) the same `taskType` is not already registered by another handler;
   - (b) `typeof(T)` declares `[ImplicitStreamSubscription("events.ExecuteCustomTaskEvent.<taskType>")]` matching the helper `WorkflowEventStreams.GetExecuteCustomTaskNamespace(taskType)`.

5. **Tests.** Write unit tests for the plugin's logic (call `ExecuteAsync` directly with stub inputs); end-to-end TestCluster integration is exercised by manual test plan #37 once a real plugin ships.

6. **Documentation.** Update `website/src/content/docs/concepts/custom-tasks.md` with the plugin's parameter schema and any limitations.

## External plugin hosts

To host your plugin in its own silo (rather than alongside the engine), use the template repo: <https://github.com/nightBaker/fleans-custom-worker-example>. Click *Use this template*, set `Fleans:Role=Plugin`, call `siloBuilder.AddFleansPluginHost(builder.Configuration)` from `Fleans.Worker.Hosting` in your `Program.cs`, register plugins via `services.AddXxxPlugin()`. See [placement-and-roles.md](./placement-and-roles.md) for the role/placement contract.
