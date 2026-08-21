# Viegard Architecture

> **Status: APPROVED by Hannah on 2026-08-18 (D-0017), including assumptions 1-5.**
> Approved decisions live in [DECISIONS.md](DECISIONS.md).  Open questions live in [TODO.md](TODO.md).

Viegard is a modular, self-hosted autonomous monitoring and security platform with local AI inference.  This document proposes the component architecture, solution layout, core interfaces, data model, deployment topology, and security boundaries.

## Assumptions

Assumptions made in this proposal that Hannah should confirm or correct:

1. **Two-container deployment is acceptable** on the Debian 13 Docker host (pipeline host + admin API host), plus a llama.cpp container/process and, later, a database.
2. **The pipeline host exposes no inbound network listener** except a local health endpoint and, per D-0023, the guarded syslog UDP ingestion listener (source-IP allowlist, rate and size caps; splittable into a credential-free listener-only instance once cross-process queues exist).  The admin API is the only administrative HTTP surface.  Communication between the two services flows through shared persistence (reads) and a persisted command queue (writes), not a direct API on the pipeline host.
3. **In-process queues (bounded channels) are sufficient** for v1 throughput (home-scale mail volume and nginx logs).  The queue port is designed with **broker semantics from day one**: explicit acknowledge/abandon, small versioned serializable messages that carry entity IDs rather than payload object graphs, idempotent consumers, and a poison-message policy.  The in-process Channel implementation is the degenerate case, so an external broker (SQL Server Service Broker, Kafka, or another; see TODO) can replace it later without a rewrite.
4. **MailKit** is the intended IMAP library, subject to a supply-chain review before installation.
5. Default v1 deployment runs all pipeline modules **in one process**, but process topology is **extensible per service**: the pipeline host is a role-configurable binary that can be deployed N times, each instance running a configured subset of modules (see "Host roles and process topology").  Data sources are also multi-instance by configuration (e.g., ten IMAP accounts across several mail hosts, each with its own worker, credential, offsets, and health).  Horizontal scaling of a *single* role beyond one process (other than sources) is out of scope for v1.

## Layered view

```
                              VIEGARD
                                 |
        +----------------+------+--------+----------------+
        |                |               |                |
      EYES            FLIGHT           MIND            JUDGMENT
   observation      transport &     inference &         policy
   & ingestion      correlation    classification         |
        |                |               |                |
        +----------------+-------+-------+----------------+
                                 |
                     +-----------+-----------+
                     |                       |
                   TALONS                 LEDGER
                   actions                 audit
                     |
                   ROOST
             persistent state & config
```

Pipeline data flow:

```
Data Sources (IMAP, syslog/nginx, MDaemon, future)
     |
     v
Ingestion (per-source workers)                 EYES
     |
     v
Parsing / Normalization -> NormalizedEvent     EYES
     |
     v
Event Store + events queue                     FLIGHT / ROOST
     |
     v
Correlation -> Incident (episodes)             FLIGHT
     |
     v
Incidents queue                                FLIGHT
     |
     v
Deterministic Incident Classification          MIND
(always available; no inference dependency)
     |
     v
Classifications queue                          FLIGHT
     |                           Optional AI enrichment later may add
     |                           schema-validated context without blocking
     |                           deterministic flow.
     v
            Policy Engine -> Decision          JUDGMENT
                  |
                  v
            Action Engine -> ActionResult      TALONS
       (dry-run first; typed operations only)
                  |
                  v
              Audit Ledger                     LEDGER
   (every stage writes audit records)
```

Key invariants:

- Deterministic detection, ingestion, and admin access never block on, or degrade because of, AI classification.
- The AI recommends; the policy engine decides; the action engine executes only typed, validated operations.
- Every stage transition is separately auditable (retrieval vs. classification vs. recommendation vs. decision vs. modification).
- AI failure fails safe: no action, event retained, failure recorded, deterministic path unaffected.

## Deployables

Deployable | Container | Responsibility
-----------|-----------|---------------
`viegard-pipeline` | Worker Service (Generic Host) | Role-configurable host binary; deployable one or more times, each instance running a configured subset of pipeline modules (ingestion, normalization, correlation, classification, policy, actions, audit).  Holds only the credentials its configured modules need.  No inbound listener except a bind-local health endpoint.
`viegard-admin` | ASP.NET Core (Blazor Web App: static SSR + Interactive Server islands, D-0016) | Mobile-compatible admin GUI + API: read access to incidents, classifications, decisions, audit; command submission (approve/reject action, unblock IP, reclassify, retry, corrections) usable from a phone, degradable to plain form posts; queue health monitor with per-queue traffic-light status (see Observability); automated staleness detection and refresh with an explicit "data is out of date, refreshing" hint.  Mobile push deferred (D-0015).  Holds no integration credentials.
llama.cpp `llama-server` | Existing/third-party | Local inference endpoint.  Dev: small quantized Qwen-class model on CPU.  Prod: larger model on the V100 server.
Database | PostgreSQL 17 container (D-0024) | Shared persistence for events, incidents, classifications, decisions, actions, audit, commands, feedback, telemetry, and durable queues (`SKIP LOCKED` + `LISTEN/NOTIFY`); nightly `pg_dump` sidecar for DR

### Host roles and process topology (proposal)

The pipeline host executable is **role-configurable**: its configuration declares which modules the instance runs.  This makes the number of processes extensible per service without code changes.  Examples:

- v1 default: one instance running every module.
- Later: one instance per mail provider (each holding only that provider's credentials), one instance for nginx ingestion, one core instance for correlation + policy + actions.

Rules:

- **Singleton roles.**  The correlator (single writer over incident state) and the policy/action engine (guardrail counters, action rate caps, circuit breaker state must be globally consistent) run in exactly one instance.  Configuration validation rejects topologies that violate this.
- **Multi-instance roles.**  Data-source and classification modules fan out freely.  Each configured data-source instance (e.g., each IMAP account) is an isolated worker with its own connection, credential, ingestion offsets, and health contributor, regardless of which process hosts it.
- **Transport follows topology.**  Modules co-located in one process communicate over in-process bounded channels; modules split across processes use a durable queue implementation of the same `IWorkQueue` port (database-backed table queue first; an external broker can replace it later, see TODO).  Module code is identical in both topologies.

### Inter-service communication (proposal)

The admin API never calls into the pipeline process.  It reads shared persistence directly and writes **commands** (e.g., `ApproveAction`, `UnblockIp`, `RetryClassification`) to a persisted command table/queue.  The pipeline host polls/subscribes, validates each command against policy, executes, and audits.  Benefits: the pipeline exposes no attack surface, commands are durable and auditable, and manual-approval mode falls out naturally.  Trade-off: command execution is asynchronous (typically sub-second at home scale).

## Solution layout (proposed)

```
Viegard.slnx                   Solution (XML solution format; .NET 10 SDK default)
Directory.Build.props          NuGetAudit, nullable, warnings-as-errors, LangVersion
src/
  Viegard.Domain/              Entities, value objects, enums; zero external dependencies
  Viegard.Application/         Ports (interfaces), deterministic classification,
                               pipeline orchestration, policy engine, prompt assembly,
                               schema validation, guardrails
  Viegard.Persistence/         Store implementations (in-memory/file first; DB when chosen)
  Viegard.Sources.Imap/        IMAP data source adapter (MailKit)
  Viegard.Sources.Syslog/      Syslog UDP source adapter; nginx/SWAG access logs normalize
                               to HTTP request events, other tags remain generic syslog
  Viegard.Sources.MDaemonLogs/ MDaemon flat-file log source adapter; SMTP/IMAP/POP session
                               transcripts, Screening, and Dynamic Screening logs normalize
                               to MDaemon credential-attack and IP-block evidence
  Viegard.Inference.LlamaCpp/  llama-server adapter (OpenAI-compatible wire protocol lives
                               here only; never in Domain/Application)
  Viegard.Actions.Imap/        Email action provider
  Viegard.Actions.MikroTik/    RouterOS address-list action provider
  Viegard.Actions.Fail2Ban/    Fail2Ban integration (mode TBD)
  Viegard.Notifications.Email/ Operator status/alert emails via SMTP (MailKit)
  Viegard.PipelineHost/        Worker service executable, including ingestion,
                               correlation, classification, and policy workers
  Viegard.AdminApi/            Admin API executable
tests/
  Viegard.Domain.Tests/
  Viegard.Application.Tests/   Policy, guardrails, deterministic classification,
                               schema validation, prompt injection
  Viegard.Sources.Imap.Tests/
  Viegard.Sources.Syslog.Tests/       Syslog and nginx parser tests
  Viegard.Sources.MDaemonLogs.Tests/  Sanitized MDaemon parser fixtures
  Viegard.Integration.Tests/   Inference, ingestion, action providers (no real credentials)
  fixtures/                    nginx log corpora, representative emails, malformed AI output
docs/
deploy/                        Dockerfiles, sanitized compose examples
```

Adapters are separate projects so integrations stay optional, independently testable, and additive: new sources/actions never modify the core.  Project count is higher, but each project is small.

Implemented source integrations:

- IMAP mail source, using per-account configuration and read-only folder access.
- Syslog UDP source, with source allowlist, size cap, rate cap, RFC 3164/5424 parsing, and nginx access-log normalization.
- MDaemon flat-file log source, intended for the Windows satellite pipeline instance on the MDaemon host.  It tails configured per-day log patterns, stores byte offsets per file, baselines existing files by default, skips session-log banners, drops Dynamic Screening noise by default, and normalizes SMTP/IMAP/POP, Screening, and Dynamic Screening lines into shared IP-correlatable events.

## Core interfaces (ports; final shapes at implementation)

Interface | Metaphor | Contract summary
----------|----------|-----------------
`IDataSource` | Eyes | Produces `RawObservation`s from an external system; reports health and ingestion offsets
`IEventParser` / `IEventNormalizer` | Eyes | Raw payload -> `NormalizedEvent`; malformed input yields a parse-failure event, never an exception escape
`IEventStore`, `IIncidentStore`, ... | Roost | Persistence ports; DB-agnostic
`ICorrelator` | Flight | Folds `NormalizedEvent`s into `Incident`s by configurable dimensions (IP, subnet, window, host, URI family, User-Agent)
`IClassifier` | Mind | Incident/message -> `Classification`; implementations: deterministic rules, heuristics, allow/denylists, AI-backed
`IInferenceProvider` | Mind | Provider-neutral structured inference: domain request (template id + variables + output schema) -> schema-validated domain response.  No OpenAI types.
`IPolicyEngine` | Judgment | (`Classification`, context, guardrail state) -> `Decision` (Permit / Deny / RequireApproval / DryRun) with matched-policy provenance
`IActionProvider` | Talons | Executes a closed catalog of typed operations; validates inputs; returns `ActionResult` with rollback info
`INotificationProvider` | Talons | Operator notification delivery; initial implementation: operator email (SMTP).  Mobile push mechanism deferred pending privacy review (D-0015); the port stays pluggable for it
`IAuditLedger` | Ledger | Append-only audit records covering every stage
`ISecretProvider` | Roost | Named secret retrieval; file-mounted (prod) and user-secrets (dev) implementations
`IHealthContributor` | - | Per-component health surfaced by both hosts
`ICommandQueue` | - | Durable admin-to-pipeline commands
`IWorkQueue` | Flight | Broker-semantics work queue port (ack/abandon, serializable messages, idempotent consumers); in-process bounded-channel implementation first

## Event and decision model (proposed)

Common envelope, single normalized representation across sources:

Type | Key fields
-----|-----------
`RawObservation` | id, source id, observed-at, raw payload reference, ingest offset
`NormalizedEvent` | id, source type, occurred-at, entity refs (IP, email address, host, URI, user), typed payload (`HttpRequestEvent`, `MailMessageEvent`, ...), raw reference
`Incident` | id, correlation key/dimensions, time window, member event ids, evidence items with scores, state
`Classification` | id, subject (incident or message), classifier id, model + version + prompt/template version (when AI), category, confidence, severity, reasons, recommended action, uncertainty; **schema-validated before use**
`Decision` | id, classification id, policy id + version, outcome, rationale, guardrail evaluations (protected lists, rate caps, circuit breaker, dry-run, approval mode)
`ActionRecord` | id, decision id, provider, operation, parameters, result, error, rollback info, timestamps
`AuditRecord` | Links the entire chain: event -> incident -> classification -> decision -> action; answers "why was this IP blocked?" / "why was this email moved?" without raw-log reconstruction
`Correction` | Human feedback (AI said X, Hannah said Y), stored separately from the original classification

Typed payloads keep source-specific detail out of the shared envelope, so mail, nginx, Fail2Ban, MikroTik, and Windows events correlate without inventing incompatible representations.

## Security boundaries

Boundary | Rule
---------|-----
Untrusted data | All observed content (bodies, subjects, URLs, User-Agents, filenames, log lines) is data, never instructions.  It enters prompts only inside clearly delimited untrusted-data blocks via prompt templates; it never reaches shell, SQL, RouterOS, or file paths unescaped.
Inference | Prompt assembly separates SYSTEM / APPLICATION / UNTRUSTED-OBSERVED-DATA.  Model output is parsed against a strict schema; anything malformed, incomplete, oversized, or contradictory is discarded and recorded as an AI failure.  Local-only by default; no silent fallback to remote.
Policy | The policy engine is the only path to actions.  Guardrails (protected addresses/networks/hosts, action rate caps, max ban duration, cooldowns, circuit breaker, emergency stop, dry-run, approval mode) are enforced here and cannot be bypassed by any classifier.
Actions | Providers expose a closed catalog of typed operations (e.g., `AddAddressListEntry(ip, list, ttl)`), never command strings.  IP syntax, private/reserved ranges, and protected lists are validated at this layer too (defense in depth).  Destructive operations (mail delete, firewall change) ship disabled and require explicit configuration.
Admin | Separate process; authn/authz model TBD (open question).  No integration credentials in this process.  Commands are durable, validated, and audited; the admin API cannot invoke actions directly.
Secrets | `ISecretProvider` only.  Never in source, config in git, logs, prompts, exceptions, telemetry, audit records, or docs.

## Failure handling

- **AI unavailable/timeout/malformed:** classification marked failed, event/incident retained, deterministic rules keep operating, retries per configurable policy, failure visible in health status.  Never more permissive on failure.
- **Parse failures:** recorded as malformed-record events; ingestion continues.
- **Action failures:** recorded with error and rollback info; circuit breaker counts them.
- **Backpressure:** bounded queues; ingestion offsets persist so restarts resume without loss or duplication.

## Observability

Both hosts expose health endpoints (liveness + per-component readiness: IMAP connection, log ingestion, inference backend, queue depth, action providers).  Metrics (classification throughput, inference latency, action counts, failures, blocked-IP count, AI errors, policy decisions) via a mechanism to be chosen with Hannah if it materially affects deployment (open question).  Structured logging via `Microsoft.Extensions.Logging` abstractions; sink/format choices deferred.

### Queue health monitor (traffic-light)

The admin API/GUI displays a per-queue traffic-light status so stalled or lagging queues are immediately visible.  Requirement from Hannah (D-0012).

- **Signals per queue:** depth (absolute and vs. capacity), age of the oldest unacknowledged message (the primary timeliness signal), consumer heartbeat/liveness, throughput trend, recent poison-message count.
- **Status derivation (thresholds configurable):** green = consumers alive and oldest-message age below the amber threshold; amber = lag or depth above threshold, or recent poison messages; red = no live consumer heartbeat, oldest-message age above the red threshold, or circuit breaker open.
- **Transport-independent:** pipeline instances publish per-queue telemetry and heartbeats to shared persistence on a short interval; the admin API computes status from those records and treats stale telemetry itself as red (detects a dead pipeline process even when a queue is empty).  For DB-backed queues the admin API can additionally measure depth and oldest-message age directly from the queue table, independent of the producer.

## Proposed initial dependencies (each requires supply-chain review before install)

Package | Purpose | Note
--------|---------|-----
MailKit | IMAP over TLS | De facto standard .NET IMAP library
xUnit (+ built-in asserts) | Testing | Avoids FluentAssertions v8 commercial-license issue
System.Threading.Channels | Bounded in-process queues | Part of the BCL, no external dependency

Everything else (EF Core or alternative, metrics exporter, notification client) waits for the corresponding decision.

## Open questions affecting this proposal

Tracked in TODO.md; the architecture keeps them behind interfaces so implementation can start on the skeleton while they are resolved: database technology, SWAG log transport, Yahoo auth + IDLE vs. polling, MikroTik API/auth, Fail2Ban mode, admin authn/authz, notifications, retention, thresholds, protected ranges, CI/CD.
