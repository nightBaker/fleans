# Test Audit — Findings Report

**Scope:** `src/Fleans/Fleans.*.Tests` (175 test files, ~37k lines, excluding `Fleans.E2E.Tests`)
**Date:** 2026-06-19
**Run mode:** scheduled routine — **no issues filed**, awaiting approval per skill step 4.

## Headline

The fleans test suite is, on the whole, **well-curated**. The classic AI-generated-noise patterns the skill targets (smoke-test `Assert.IsNotNull(result)`, swallowed-`try/catch`, over-mocked services that assert the mock setup) are largely **absent**. The wins are concentrated in a handful of config/options test files where adjacent test methods are clearly `[DataTestMethod]` candidates — these were written as a documented case matrix rather than over-generated, so "merge" is more accurate than "delete."

**Net effect of acting on all findings:** ~25 test methods collapse into ~7 parameterized methods. Cuts maybe 18 methods (≈10% reduction in the audited files), no coverage loss.

## Findings

### 1. DUPLICATE across files — `KafkaProductionPresetTests` ↔ `KafkaStreamingOptionsTests`

- **Files:**
  - `src/Fleans/Fleans.Infrastructure.Tests/KafkaProductionPresetTests.cs:53-69`
  - `src/Fleans/Fleans.Streaming.Kafka.Tests/KafkaStreamingOptionsTests.cs:12-29`
- **Action:** delete the duplicate pair in `KafkaProductionPresetTests`.
- **Why:** `Default_QueueCount_is_8_without_preset` and `Default_NumPartitions_is_1_without_preset` assert the *exact* same thing as `QueueCount_default_is_8` / `NumPartitions_default_is_1` in the streaming options file — fresh `KafkaStreamingOptions()`, same property. The "regression guard, preset is opt-in" framing is misleading because the preset isn't even instantiated; it's a pure default-value check that already lives in the options test file.

### 2. MERGE — `KafkaProducerConfigBindingTests.MapAcks_*_roundtrip`

- **File:** `src/Fleans/Fleans.Streaming.Kafka.Tests/KafkaProducerConfigBindingTests.cs:71-93`
- **Tests involved:** `MapAcks_All_roundtrip`, `MapAcks_Leader_roundtrip`, `MapAcks_None_roundtrip`
- **Action:** collapse into one `[DataTestMethod]` with three `[DataRow]`s — `(KafkaAcks.All, Acks.All, enableIdempotence: true)`, `(KafkaAcks.Leader, Acks.Leader, false)`, `(KafkaAcks.None, Acks.None, false)`.
- **Why:** identical structure, only the enum value differs.

### 3. MERGE — `KafkaProducerConfigBindingTests.EnableIdempotence_with_Acks_*_throws`

- **File:** same as above, `:30-56`
- **Tests:** `EnableIdempotence_with_Acks_Leader_throws`, `EnableIdempotence_with_Acks_None_throws`
- **Action:** `[DataTestMethod]` with two `[DataRow]`s on `KafkaAcks`.
- **Why:** identical structure.

### 4. MERGE — `RedisStreamingOptionsTests` invalid-value throws

- **File:** `src/Fleans/Fleans.ServiceDefaults.Tests/RedisStreamingOptionsTests.cs:55-85`
- **Tests:** `Throws_when_TotalQueueCount_is_zero`, `Throws_when_TotalQueueCount_is_negative`
- **Action:** `[DataTestMethod]` with `[DataRow("0")]`, `[DataRow("-3")]`. Keep the non-integer case separate — it asserts a different error message ("must be an integer" vs ">= 1").
- **Why:** identical structure, differ only by string value.

### 5. MERGE — `KafkaClientConfigExtensionsTests` SASL credential validation cluster

- **File:** `src/Fleans/Fleans.Infrastructure.Tests/KafkaClientConfigExtensionsTests.cs:187-255`
- **Tests:** `SaslPlaintext_Plain_null_username_throws`, `SaslPlaintext_Plain_empty_username_throws`, `SaslPlaintext_Plain_null_password_throws`, `SaslPlaintext_Plain_empty_password_throws`
- **Action:** one `[DataTestMethod]` over `(username, password)` with `[DataRow(null, "pass")]`, `[DataRow("", "pass")]`, `[DataRow("user", null)]`, `[DataRow("user", "")]`.
- **Why:** four tests, same throw, same setup — only which credential is null/empty differs. 4 → 1.

### 6. MERGE — `KafkaClientConfigExtensionsTests` SSL invalid-combo cluster

- **File:** same, `:373-413`
- **Tests:** `S5_Ssl_cert_without_key_throws`, `S6_Ssl_key_without_cert_throws`, `S7_Ssl_password_without_key_throws`
- **Action:** one `[DataTestMethod]` parameterized over which of `SslCertificateLocation` / `SslKeyLocation` / `SslKeyPassword` is set in isolation.
- **Why:** structural twins. 3 → 1.

### 7. MERGE — `KafkaClientConfigExtensionsTests` OAuthBearer pair

- **File:** same, `:112-152`
- **Tests:** `SaslSsl_OAuthBearer_sets_protocol_and_mechanism_without_credentials`, `SaslPlaintext_OAuthBearer_sets_protocol_and_mechanism_without_credentials`
- **Action:** `[DataTestMethod]` over `SecurityProtocol`.
- **Why:** identical except for one enum value.

### 8. MERGE — `KafkaProductionPresetTests.QueueCount` / `NumPartitions` curve

- **File:** `src/Fleans/Fleans.Infrastructure.Tests/KafkaProductionPresetTests.cs:14-49`
- **Tests:** `QueueCount_returns_8_when_cores_below_floor`, `QueueCount_returns_8_at_exactly_8_cores`, `QueueCount_scales_linearly_above_8_cores` (and the `NumPartitions_*` siblings)
- **Action:** two `[DataTestMethod]`s — one for `QueueCount`, one for `NumPartitions` — with the cores/expected pairs as `[DataRow]`s. The two methods inside already each multi-assert; this just promotes the pattern.
- **Why:** the whole "curve" is already implicitly a parameterized test, just inlined as `Assert.AreEqual` triplets. Cleaner as data rows.

### 9. SIMPLIFY (low-priority) — `McpToolRegistrationTests.AllToolClasses_HaveMcpServerToolTypeAttribute`

- **File:** `src/Fleans/Fleans.Mcp.Tests/McpToolRegistrationTests.cs:11-21`
- **Action:** drop the `toolTypes.Count >= 2` assertion. The two subsequent `CollectionAssert.Contains` calls already prove both classes are present and would also fail if the count were < 2.
- **Why:** redundant assertion; not a deletion but a cleanup.

### 10. MERGE (borderline) — `McpToolRegistrationTests.WorkflowTools_ExposesExpectedTools` ↔ `InstanceTools_ExposesExpectedTools`

- **File:** same, `:23-49`
- **Action:** `[DataTestMethod]` parameterized over `(toolClassType, expectedToolNames[])`.
- **Why:** structurally identical, only the `typeof(...)` and expected name array differ. Judgment call — leaving as two methods isn't wrong (they enumerate different APIs), but a single parameterized test is more compact.

## What I did **not** find

To set expectations:

- **No swallowed try/catch tests.** The only `try`/`catch`-in-test pattern is `EfCoreEventStoreTests.cs:318-320`, which captures-and-asserts (`Assert.IsNotNull(caught)`) — legitimate, just slightly old-school.
- **No `Assert.IsNotNull(result)`-only smoke tests.** Every hit on that pattern is followed by substantive assertions on the result's content.
- **No over-mocked tests.** The handful of `Received(...)`/`Verify(...)` calls in `Effects/*EffectHandler*Tests.cs` verify the handler's *contract* (dispatch to the correct sibling grain method) — which is exactly the right behavior to assert for a dispatcher.
- **No widespread duplicate-only-different-name pairs** in the domain tests. `WorkflowDefinitionErrorEventSubProcessTests`, `WorkflowInstanceStateTests`, etc. each method tests a distinct branch.

## Sampled files (full read)

- `KafkaStreamingOptionsTests.cs`, `KafkaProducerConfigBindingTests.cs`, `KafkaProductionPresetTests.cs`, `KafkaClientConfigExtensionsTests.cs`
- `RedisStreamingOptionsTests.cs`, `McpToolRegistrationTests.cs`
- `WorkflowDefinitionErrorEventSubProcessTests.cs`, `WorkflowInstanceStateTests.cs` (partial), `WorkflowQueryServiceTests.cs` (partial)
- `BpmnConverter/{GatewayTests,SubProcessTests}.cs`
- `Effects/SignalEffectHandlerTests.cs`

## Not yet swept (recommend before filing)

The audit was a targeted sample, not exhaustive. The largest files I did *not* fully read:
- `WorkflowExecutionTransitionTests.cs` (1344 lines), `WorkflowExecutionScopeCompletionTests.cs` (1107), `WorkflowExecutionBoundaryTests.cs` (1066), `SubProcessTests.cs` Application (797), `EscalationEventTests.cs` (756) — these are where any remaining bulk duplication likely lives. Worth a second pass if you want a fuller report before filing.

## Suggested next step

Two reasonable paths:

1. **File the 10 findings above as issues** (`[test-audit]`-prefixed) — I have the body text ready and can do it once you approve.
2. **Have me sweep the 5 largest WorkflowExecution test files first** (the bulk of the suite), then file a consolidated batch. Probably the better call given how clean the rest of the suite looks — the high-value findings would be in there if anywhere.

Reply with which you'd like, or trim the list before I file.
