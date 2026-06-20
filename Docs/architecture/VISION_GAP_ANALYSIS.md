# OxyTrader AI Vision Gap Analysis

## 1. Purpose and Scope

This document reviews `TARGET_ARCHITECTURE_PHASE2.md` against the stated long-term OxyTrader AI vision. It identifies alignment, missing concepts, scalability concerns, and decisions that could create future migration cost.

This is not a rewrite of the Phase 2 architecture. Recommendations are limited to modifications that should be considered before or during its implementation so later capabilities remain feasible.

## 2. Overall Assessment

The Phase 2 target is a strong foundation for the core platform. It aligns well with the desired React/ASP.NET Core stack, modular-monolith strategy, PostgreSQL, canonical tick pipeline, canonical optimized candle engine, first-class replay, advisory-only AI, and evidence-based RabbitMQ deferral.

The largest gap is that the target is still modeled as a single global trading workspace. It has shared market data, but it does not yet define the long-term boundary between:

- globally shared market facts;
- user-owned broker connections;
- profile-owned strategy, risk, journal, watchlist, layout, and conversation state;
- replay/backtest run state;
- the separate AI research ecosystem.

Four decisions should be corrected before the PostgreSQL schema and module contracts become difficult to change:

1. Introduce a minimal Strategy Profile ownership model.
2. Replace broker-specific instrument-token identity with canonical instrument identity plus provider mappings.
3. Define one canonical candle/history store instead of separate live and historical candle stores containing overlapping facts.
4. Separate candle generation from strategy execution and introduce versioned strategy/run identities.

Most other future capabilities can remain unimplemented until later phases as long as these foundations are present.

## 3. Vision Alignment Matrix

| Vision item | Alignment | Assessment |
|---|---|---|
| React frontend + ASP.NET Core backend | Strong | Directly targeted through React, REST, SignalR, and an ASP.NET Core host |
| Modular monolith first | Strong | One deployable backend with modules and architecture tests; no premature microservices |
| `TickPipeline` remains the backbone | Strong | Explicit retention contract preserves channels, partitioning, batching, metrics, and shutdown |
| `InstrumentContextOptimized` remains canonical | Strong | It is retained as the sole candle engine and protected through characterization tests |
| Shared market-data model | Partial | One normalized tick path is defined, but live and historical candles are stored separately and no explicit global-versus-profile ownership rules exist |
| Strategy Profiles | Missing | No Profile module, profile identity, strategy instance, risk policy, journal, watchlist, layout, or AI conversation model |
| Profiles share market facts | Partial | Current modules are globally shared, but the invariant that profiles must never duplicate ticks/candles/instruments/history is not stated or enforced |
| Replay and backtesting first-class | Partial | Replay is strong and isolated; backtesting, strategy parameter runs, reproducibility, comparison, and result metrics are absent |
| AI advisory only | Strong | The target explicitly prohibits AI order, signal, subscription, configuration, and risk authority |
| Separate AI ecosystem | Weak/Partial | A generic in-process Advisory module exists, but Ollama, Qdrant, embeddings, RAG, research workflows, conversations, and a separate AI service boundary are not represented |
| Multiple users/brokers, marketplace, visual builder | Missing | Authentication is mentioned, but identity, tenancy, broker abstraction, strategy packaging/versioning, and builder/runtime contracts are absent |
| PostgreSQL first; RabbitMQ only when proven | Strong | PostgreSQL is explicit and RabbitMQ is deliberately deferred behind measurable need |

## 4. Missing Concepts

### 4.1 Strategy Profiles

The target has global Signals and Trading modules but no unit representing a user's independent trading workspace.

Missing profile concepts include:

- stable `ProfileId`;
- profile name, status, owner, and membership;
- enabled strategy instances and their parameter versions;
- profile risk policy and risk-policy version;
- profile journal entries and links to signals/orders/replay runs;
- watchlists and alert preferences;
- frontend layouts and saved views;
- AI conversation threads and advisory history;
- paper/live mode permissions;
- profile-specific broker-account selection;
- profile lifecycle: create, clone, archive, activate, and deactivate.

Without this boundary, signals, orders, risk, replay results, and AI records will become global records that later require invasive ownership migrations.

### 4.2 Explicit Shared-versus-Owned Data Rules

The vision requires profiles to share market data, candles, instrument master, and historical data. The target implies global modules but does not establish this as a data invariant.

The architecture needs an explicit ownership matrix:

| Scope | Data |
|---|---|
| Globally shared market facts | Canonical instruments, provider instrument mappings, normalized ticks, canonical candles, market sessions/calendars, historical coverage/import provenance |
| Profile-owned state | Strategy instances, risk settings, watchlists, journals, layouts, signals/decisions, orders, positions, AI conversations, advisories |
| User-owned state | Identity, profile memberships, broker connections, credential references, user preferences |
| Run-owned state | Replay/backtest definition, immutable input snapshot, progress, trades, metrics, logs, and results |

No profile-specific component should open a market-data socket or create its own live candle context. Active profiles consume the same canonical candle event.

### 4.3 Canonical Historical Data Model

The target proposes `candles.live_candles` and `candles.historical_candles`. That can store the same instrument/interval/time candle twice and conflicts with the vision's “candles generated once” and “historical data stored once” principles.

A missing policy must define:

- whether a candle is authoritative from `InstrumentContextOptimized`, broker history, or reconciliation;
- how historical imports backfill gaps without duplicating live-generated candles;
- how corrections/revisions are represented;
- how import provenance and data quality are audited without making a second authoritative candle store;
- whether replay reads the exact same canonical candle rows used by live charts and strategies.

### 4.4 Strategy Runtime and Versioning

The target defines one hard-coded deterministic strategy inside the Signals module. It does not define a general strategy execution contract.

Missing concepts include:

- `StrategyDefinitionId` and immutable `StrategyVersionId`;
- profile-owned `StrategyInstanceId`;
- validated parameter schema and parameter snapshot;
- required instruments, candle intervals, indicators, and warm-up data;
- strategy lifecycle and enabled status;
- deterministic strategy output contract;
- strategy provenance on signals, orders, replays, and backtests;
- compatibility/version rules for future marketplace packages;
- a safe intermediate representation for a future visual builder.

The current conservative EMA/RSI/ATR strategy should remain the first implementation, not the permanent shape of the strategy subsystem.

### 4.5 Backtesting

Replay is defined well, but backtesting is not simply replay with a faster clock.

Missing backtesting concepts include:

- immutable `BacktestRunId`;
- strategy and parameter snapshots;
- profile/risk snapshot used for the run;
- canonical dataset/version or market-data cutoff;
- deterministic random seed where simulation requires randomness;
- warm-up period distinct from evaluation period;
- slippage, fees, fill, lot-size, and liquidity assumptions;
- batch parameter sweeps;
- performance metrics and equity curves;
- result comparison and reproducibility;
- bounded concurrent execution so research cannot starve live processing.

Replay is an interactive market/session simulation. Backtesting is a reproducible research workload. They should share the market/strategy/trading kernel but have distinct application models.

### 4.6 Separate AI Ecosystem

The target's Advisory module correctly protects execution, but it models AI as an optional worker inside the modular monolith. The vision calls for a separate ecosystem.

Missing boundaries include:

- an OxyTrader Advisory Gateway in the ASP.NET Core backend;
- an external AI Research service boundary;
- Ollama as the model-runtime adapter;
- Qdrant as the vector-index adapter;
- embedding generation and versioning;
- RAG ingestion, chunking, provenance, and retrieval policies;
- profile-scoped conversation threads;
- research documents and source citations;
- prompt/model/retrieval audit metadata;
- authorization rules governing which profile data can be retrieved;
- retention/deletion behavior across PostgreSQL and Qdrant.

PostgreSQL should remain authoritative for conversations, document metadata, access control, advisory records, and provenance. Qdrant should hold derived vector representations and identifiers pointing back to authoritative records.

### 4.7 Multiple Users

The target mentions authentication and a requesting user/session, but it does not define identity or authorization ownership.

Missing concepts include:

- `UserId` and profile membership;
- roles and permissions;
- profile-level authorization checks;
- isolation of SignalR groups and query results;
- audit identity for configuration, strategy, risk, order, and advisory actions;
- broker credential ownership and encrypted secret references;
- quotas for replay, backtest, AI, and live strategy workloads;
- deletion/export semantics for user-owned content.

PostgreSQL row-level security is optional, but application-level profile scoping and integration tests are mandatory if multiple users are expected.

### 4.8 Multiple Brokers

The target remains Kite-centric and uses `InstrumentToken` as the central identity and partition key. Zerodha tokens are provider identifiers, not universal instrument identifiers.

Missing concepts include:

- canonical `InstrumentId` independent of a broker/data provider;
- mappings such as `(ProviderId, ExternalInstrumentId) -> InstrumentId`;
- market-data provider and broker-execution adapter contracts;
- broker account/connection identity;
- normalized order types, statuses, rejection reasons, and capabilities;
- provider-specific extension metadata kept outside the core domain;
- explicit choice of authoritative market-data provider per instrument/session;
- reconciliation when multiple providers publish the same instrument.

`TickPipeline` can still partition on a stable canonical instrument key. The source token must remain metadata, not become the permanent identity of shared market data.

### 4.9 Strategy Marketplace and Visual Builder

No marketplace or visual-builder concepts are present. Full implementation is appropriately deferred, but the long-term compatibility surface is missing.

The future requires:

- immutable strategy packages and versions;
- publisher/ownership and trust status;
- manifest, parameter schema, data requirements, and compatibility metadata;
- review/signing/install/enable lifecycle;
- sandbox and resource-limit policy for non-built-in strategies;
- a visual graph/DSL that compiles to a validated strategy definition;
- separation of strategy definition from profile strategy instance;
- migration behavior when a strategy version changes.

A marketplace should never be modeled as arbitrary assemblies automatically loaded into the live trading process without validation and isolation.

## 5. Future Scalability Concerns

### 5.1 One ASP.NET Core Process Owns Every Workload

The target places live ingestion, pipeline processing, PostgreSQL writes, HTTP, SignalR, historical imports, replay, and AI advisory work in one process. This is acceptable for a modular monolith, but unbounded research workloads could affect live-market latency.

Required in-process workload controls include:

- priority for live ticks, candles, risk, and exits;
- separate bounded queues for live processing, persistence, replay, backtesting, imports, SignalR, and AI requests;
- per-workload concurrency limits;
- cancellation and execution deadlines;
- PostgreSQL connection-pool budgets per workload;
- memory/CPU quotas or admission control for replay/backtest jobs;
- load shedding for non-critical UI and advisory updates;
- readiness degradation before live correctness is threatened.

### 5.2 Horizontal Scaling Can Duplicate the Market Engine

Running two identical backend instances would start two Kite connections, two `TickPipeline` instances, and two canonical candle engines. That violates “ticks generated once” and “candles generated once.”

Phase 2 should explicitly support one active market-processing instance. Future horizontal API scaling must not casually replicate the feed owner. If metrics later require separation, market processing is a candidate extraction boundary; it should not be duplicated behind a normal web load balancer.

### 5.3 Profile Fan-Out

One candle may eventually feed many profiles and many strategy instances. Synchronous evaluation of every strategy on the candle-processing thread would make candle latency proportional to profile count.

The future needs:

- one canonical candle publication;
- an indexed registry of strategy instances interested in that instrument/interval;
- bounded strategy-evaluation scheduling;
- deterministic ordering within each strategy instance;
- profile and strategy quotas;
- slow-strategy detection and isolation;
- no rebuilding of candles per profile.

### 5.4 Tick and Candle Storage Growth

The target mentions a tick retention policy but not a physical data-lifecycle design. Raw ticks will dominate PostgreSQL volume.

Concerns include:

- time-based table partitioning;
- BRIN/B-tree index choices by query pattern;
- retention and archival tiers;
- vacuum/autovacuum behavior under bulk ingest;
- import and replay queries competing with live writes;
- backup/restore time;
- candle revision history and deduplication;
- aggregate/query read models for charts and research.

These should be measured before adding a time-series extension or another database. PostgreSQL remains the initial system of record.

### 5.5 SignalR Fan-Out

A single market hub is sufficient initially, but pushing every tick to every browser will not scale.

The frontend boundary needs subscription-aware groups, LTP coalescing, rate limits, reconnect snapshots, stale-data indicators, and no assumption that SignalR delivers every market event. Browsers consume a presentation stream; they do not consume the authoritative event stream.

### 5.6 Replay and Backtest Data Access

Concurrent large scans can degrade live ingestion and candle writes. Streaming alone does not provide workload isolation.

The architecture will eventually need query budgets, bounded run concurrency, cancellation, suitable indexes/partitions, optional read replicas only when measured, and immutable run metadata that permits offline reruns.

### 5.7 AI Index Growth and Consistency

Qdrant introduces a derived data store whose index can drift from PostgreSQL. Embedding/model changes also invalidate previous vector representations.

The AI ecosystem needs idempotent indexing, embedding version fields, source checksums, re-index jobs, deletion propagation, access-control filters, and health metrics. Qdrant must be rebuildable from authoritative PostgreSQL/document sources.

### 5.8 Broker and Provider Rate Limits

Multiple users and profiles must not produce duplicate subscriptions or uncontrolled broker calls. A subscription coordinator should compute the union of active instrument requirements and manage provider limits centrally.

## 6. Decisions That Would Make Evolution Difficult

| Current Phase 2 decision | Future difficulty | Severity |
|---|---|---|
| Using `InstrumentToken` as the universal market-data identity | Locks persisted ticks/candles and partitioning to Zerodha identifiers; multi-broker migration becomes pervasive | Critical |
| Separate `live_candles` and `historical_candles` authoritative tables | Permits duplicate/contradictory candles and forces every consumer to choose or merge sources | Critical |
| No `ProfileId` or ownership model | Makes later profile/user migration touch signals, risk, orders, journals, layouts, replay, and AI records | Critical |
| `InstrumentContextOptimized` retains strategy evaluation responsibility | Couples canonical shared candle generation to one strategy and obstructs many profile strategy instances | High |
| Hard-coded Signals module without strategy definition/version/instance identity | Prevents reproducible backtests, safe upgrades, marketplace packages, and visual-builder output | Critical |
| Replay results mixed into general signal/trade tables only through nullable `ReplayId` | Makes run isolation, comparison, retention, and reproducibility ambiguous | High |
| Advisory worker hosted as the entire AI model | Encourages Ollama/Qdrant/RAG concerns to leak into the trading backend | High |
| One central `Contracts` assembly for all modules | Can become a shared-model dumping ground that tightly couples modules and makes later extraction difficult | Medium |
| One central PostgreSQL infrastructure project implementing every repository | Can bypass module ownership and evolve into a replacement for the current monolithic `DataAccess` class | High |
| Direct module dependency arrows such as Trading -> Signals -> Candles | Risks deep synchronous coupling and cyclic growth as profiles, marketplace, and backtesting are added | Medium/High |
| One host runs unbounded live, replay, import, backtest, and AI work | Research or UI demand can increase live-market latency | Critical |
| Assuming the whole backend can be horizontally replicated | Duplicates feed connections, ticks, candles, and signals | Critical |
| AI advisory modeled mainly as attached to a signal | Too narrow for profile conversations, document research, market research, and replay analysis | Medium |
| No explicit active-subscription union across profiles | Encourages duplicate market feeds or profile-specific market-data generation | High |

## 7. Recommended Modifications

### 7.1 Required Before PostgreSQL Schema Finalization

#### A. Add a minimal Profiles module and ownership contract

Add Profile concepts to the target solution and data model even if Phase 2 ships with one user and one default profile.

Minimum foundation:

- `ProfileId` on strategy instances, risk policies, journals, watchlists, layouts, conversations, advisories, signals, orders, positions, and replay/backtest requests where applicable;
- a default profile migration for current data;
- profile authorization context in REST, SignalR, and application commands;
- an explicit statement that ticks, candles, instruments, and historical coverage never carry `ProfileId`;
- profile subscription requirements resolved into one global subscription union.

This does not require completing multi-user support in Phase 2. It prevents global-state assumptions from becoming permanent.

#### B. Introduce canonical instrument identity

Add a broker-neutral `InstrumentId` and retain Kite token as a provider mapping.

Recommended identity layers:

- `InstrumentId`: internal stable key used by ticks, candles, strategies, replay, and pipeline partitioning;
- `ProviderId`: Kite, future data provider, or future broker;
- `ExternalInstrumentId`: provider token/symbol identifier;
- `InstrumentMapping`: effective dates and provider metadata.

`TickData` should carry both canonical instrument identity and source metadata. The existing token may remain during migration but should not be the long-term foreign key.

#### C. Define one canonical candle store

Replace the two-authoritative-table concept with one canonical candle identity:

`(InstrumentId, Interval, CandleOpenTimeUtc)`.

Live generation and historical backfill should upsert the same logical record under a documented authority/reconciliation policy. Import run, provider, source timestamp, quality, and revision metadata can be stored alongside the candle or in provenance tables. Replay, charts, strategies, and research should query the same candle store.

If physical hot/archive partitions are later needed, they should remain one logical data contract rather than separate live/history semantics.

#### D. Add strategy definition, version, and instance identities

Define the minimum strategy contract now:

- immutable strategy definition/version;
- profile-owned configured instance;
- parameter schema and validated parameter snapshot;
- input requirements;
- deterministic evaluation result;
- strategy/run provenance on every signal and order.

Register the current conservative strategy as the initial built-in version. Do not implement a marketplace or visual builder yet.

#### E. Define a common run context

Introduce explicit execution context fields rather than relying only on nullable `ReplayId`:

- `RunMode`: Live, Replay, Backtest;
- `RunId` for non-live execution;
- `ProfileId`;
- `StrategyInstanceId` and `StrategyVersionId`;
- market-data cutoff/dataset identity;
- configuration/risk snapshot identity.

This provides reproducibility and clean separation without requiring a full backtest engine in Phase 2.

### 7.2 Required During Processing-Core Refactoring

#### F. Make `InstrumentContextOptimized` candle-only

Preserve it as the canonical candle engine, but move `EvaluateSignalsPositionAware_Conservative` behind the strategy runtime boundary after characterization tests prove parity.

The candle engine should emit canonical candle outcomes once. Strategy instances consume those outcomes. This is essential for many profiles sharing one candle while applying different strategies and risk settings.

#### G. Establish one shared market-data authority

Amend the target invariants to state:

- one provider tick is normalized once;
- accepted normalized ticks enter one `TickPipeline`;
- canonical ticks are persisted once;
- a live candle is built once per instrument/interval;
- profile strategies consume shared candle events;
- replay/backtest contexts may rebuild ephemeral candles for reproducibility but never write them as duplicate live canonical data.

Add ingestion identity/source metadata and a documented deduplication policy. “Generated once” should mean one authoritative generation path, not an unrealistic guarantee that networks never redeliver data.

#### H. Add bounded workload schedulers

Keep one modular-monolith process initially, but isolate live, persistence, replay, backtest, import, SignalR, and AI queues. Define concurrency/admission limits and metrics before enabling concurrent research workloads.

Live tick/candle/risk processing must have priority over replay, backtesting, imports, UI fan-out, and AI.

### 7.3 Required for the AI Boundary

#### I. Recast Advisory as an AI Gateway

Keep an Advisory module in the ASP.NET Core backend, but limit it to:

- authorization and profile scoping;
- context/snapshot preparation;
- request tracking;
- advisory/conversation persistence;
- citations and provenance;
- communication with a separate AI Research service;
- presentation of advisory results.

Place Ollama, Qdrant, embedding generation, retrieval pipelines, document ingestion, and research orchestration outside the trading backend. Initial communication can be HTTP/gRPC; RabbitMQ is not required.

The AI Research service must have no Trading command credentials or dependency. Its output returns only through the advisory contract.

#### J. Add profile conversations and retrieval ownership

Define `ConversationId`, `ProfileId`, messages, source documents, citations, model/prompt metadata, and retention. Qdrant points must include authoritative document/chunk IDs, embedding version, and access-scope filters.

### 7.4 Required for Clean Module Evolution

#### K. Keep contracts module-local

Avoid turning `ZerodhaOxySocket.Contracts` into a universal shared domain model. Keep public API DTOs at the API boundary and module integration contracts owned/versioned by the producing module.

Share only true primitives such as canonical identifiers, UTC time abstractions, result types, and observability interfaces.

#### L. Keep persistence module-owned

The central PostgreSQL project should provide connections, transactions, migrations tooling, and shared type mappings only. Repository implementations and migrations for a module should remain with that module or in its infrastructure assembly.

This preserves table ownership and prevents a new static `DataAccess` monolith.

#### M. Define extraction and scaling metrics without adding RabbitMQ

Record metrics that can later justify microservices or RabbitMQ:

- tick channel utilization and overflow;
- per-partition queue and processing latency;
- candle finalization lag;
- database writer queue depth and commit latency;
- strategy fan-out count and evaluation latency;
- replay/backtest queue time, CPU, memory, and database pressure;
- SignalR fan-out and dropped/coalesced updates;
- AI request queue time and indexing backlog;
- process resource saturation and failure blast radius.

Do not add RabbitMQ until these measurements demonstrate a durable cross-process delivery or independent-scaling need.

## 8. Recommended Changes by Target Document Section

| Target section | Recommended modification |
|---|---|
| Goals and principles | Add profile ownership, shared-market-data invariants, canonical instrument identity, and separation of candle generation from strategy execution |
| Target solution structure | Add Profiles, Strategy Runtime, Backtesting, Broker Integration contracts/adapters, and AI Gateway; show AI Research as an external future subsystem |
| Module responsibilities | Distinguish global Market Data/Candles/Instruments from profile-scoped Strategies/Risk/Journal/Workspace/Advisory |
| Runtime architecture | Declare one active market-processing instance and bounded workload classes inside the monolith |
| Tick data flow | Use canonical `InstrumentId`, ingestion/source identity, global subscription union, and explicit single-generation rules |
| Candle and signal flow | Make optimized context candle-only; fan canonical candles to versioned profile strategy instances |
| Trading and replay | Add ProfileId, StrategyInstanceId, run context, broker-account abstraction, and distinct backtest model |
| PostgreSQL architecture | Replace split live/history candle authority; add users/profiles, strategy versions/instances, risk, journal, watchlist, layout, conversations, backtest runs, provider mappings, and provenance |
| AI advisory boundary | Convert in-process worker concept to backend gateway plus separate Ollama/Qdrant/embeddings/RAG research ecosystem |
| Components to keep/refactor | Keep pipeline/candle engine; add extraction of strategy logic from candle engine and broker-neutral identity migration |
| Migration roadmap | Insert identity/profile/strategy/run modeling before PostgreSQL schema finalization; add backtest foundation and AI gateway after deterministic core |
| Risk analysis | Add profile data leakage, strategy isolation, duplicate feed ownership, canonical data conflict, backtest starvation, AI vector drift, and broker normalization risks |
| Acceptance criteria | Add default-profile operation, shared-data non-duplication, strategy provenance, canonical instrument mapping, and reproducible run metadata |

## 9. Priority and Deferral Guidance

### Must influence Phase 2 foundations

- canonical instrument identity and provider mappings;
- minimal Profile model and ownership columns;
- shared-versus-profile data rules;
- one canonical candle/history contract;
- candle-only `InstrumentContextOptimized` responsibility;
- strategy definition/version/instance identifiers;
- common Live/Replay/Backtest run context;
- bounded workload isolation and metrics;
- AI Gateway boundary rather than embedded AI infrastructure.

### Can be implemented after Phase 2

- full multi-user administration and collaboration;
- second broker implementation;
- marketplace catalog, billing, signing, and distribution;
- visual strategy-builder UI;
- parameter-sweep backtest UI;
- production Ollama cluster sizing;
- full document ingestion and advanced RAG workflows;
- PostgreSQL read replicas or time-series extensions;
- microservice extraction;
- RabbitMQ.

The deferred items remain feasible only if the Phase 2 identity, ownership, versioning, and canonical-data foundations are established first.

## 10. Architectural Guardrails

The following should be recorded as long-term invariants:

1. `TickPipeline` is the single live normalized market-data backbone until metrics justify a replacement or extraction.
2. `InstrumentContextOptimized` is the single authoritative live candle engine.
3. Live ticks and candles are global market facts, never copied per user or profile.
4. Profiles own strategies, risk, journals, watchlists, layouts, conversations, signals, and trading state.
5. Every signal/order/result identifies its profile, strategy instance/version, and execution context.
6. Broker/provider identifiers never replace canonical internal instrument identity.
7. Replay and backtest reuse the deterministic kernel but remain isolated from live state.
8. AI can explain and research; it cannot create authoritative signals or invoke trading commands.
9. PostgreSQL is authoritative; Qdrant is a rebuildable derived index.
10. The modular monolith remains the default deployment until measured constraints justify extraction.
11. RabbitMQ is introduced only for a demonstrated durable cross-process messaging requirement.

## 11. Final Recommendation

`TARGET_ARCHITECTURE_PHASE2.md` should remain the implementation baseline, but it needs a focused amendment before development begins. The amendment should add profile/ownership foundations, canonical instrument identity, a single candle/history authority, strategy/run versioning, backtest boundaries, and the external AI Gateway relationship.

It should not expand Phase 2 into full multi-user, multi-broker, marketplace, visual-builder, RAG, microservice, or RabbitMQ delivery. The right move is to reserve the correct seams and identifiers now, then add those capabilities only when their product phase arrives.
