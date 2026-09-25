# Viegard Architecture

> **Status: APPROVED 2026-08-18 (D-0017), including assumptions 1-5.**
> Approved decisions live in [DECISIONS.md](DECISIONS.md).  Open questions live in [TODO.md](TODO.md).

Viegard is a modular, self-hosted autonomous monitoring and security platform with local AI inference.  This document proposes the component architecture, solution layout, core interfaces, data model, deployment topology, and security boundaries.

## Assumptions

Assumptions made in this proposal, confirmed at approval (D-0017):

1. **Two-container deployment is acceptable** on the Debian 13 Docker host (pipeline host + admin API host), plus a llama.cpp container/process and, later, a database.
2. **The pipeline host exposes no inbound network listener** except a local health endpoint and, per D-0023, the guarded syslog UDP ingestion listener (source-IP allowlist, rate and size caps; splittable into a credential-free listener-only instance once cross-process queues exist).  The admin API is the only administrative HTTP surface.  Communication between the two services flows through shared persistence (reads) and a persisted command queue (writes), not a direct API on the pipeline host.
3. **In-process queues (bounded channels) are sufficient** for v1 throughput (single-operator mail volume and nginx logs).  The queue port is designed with **broker semantics from day one**: explicit acknowledge/abandon, small versioned serializable messages that carry entity IDs rather than payload object graphs, idempotent consumers, and a poison-message policy.  The in-process Channel implementation is the degenerate case, so an external broker (SQL Server Service Broker, Kafka, or another; see TODO) can replace it later without a rewrite.
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
     |
     v
Optional Local-Model Advisor                   MIND
(decorator, disabled by default, escalation-only)
     |
     v
Classifications queue                          FLIGHT
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
- The local-model advisor decorates deterministic classification and returns the same single classification record.  It is not registered as a parallel classifier.
- The AI recommends; the policy engine decides; the action engine executes only typed, validated operations.
- Every stage transition is separately auditable (retrieval vs. classification vs. recommendation vs. decision vs. modification).
- AI failure fails safe: no action, event retained, deterministic path unaffected.  The local-model advisor fails open to the deterministic classification.

## Deployables

Deployable | Container | Responsibility
-----------|-----------|---------------
`viegard-pipeline` | Worker Service (Generic Host) | Role-configurable host binary; deployable one or more times, each instance running a configured subset of pipeline modules (ingestion, normalization, correlation, classification, policy, actions, ban reconciliation, maintenance retention, audit).  Holds only the credentials its configured modules need.  No inbound listener except a bind-local health endpoint.
`viegard-admin` | ASP.NET Core (Blazor Web App: static SSR, D-0016) | Mobile-compatible admin GUI + API: local-account authentication with mandatory TOTP and WebAuthn security keys (D-0032), server-side filtered and sortable read access to incidents, classifications, decisions, and audit; decision-list trigger summaries plus inline approve/reject actions that reuse the audited review flow and return the operator to the decisions list, anchored to the next still-reviewable row with the reviewed row rebadged approved or rejected, for rapid sequential review; step-up-gated runtime configuration editors for custom signatures, retention periods, satellite database roles with one-time password display, MikroTik router registry entries with encrypted credentials and probes, and fixed-verb host upgrade requests; custom-signature previews that run the same literal matcher as the pipeline against a bounded recent-event scan without writing audit or config rows; command submission (approve/reject action, unblock IP, reclassify, retry, corrections) usable from a phone, degradable to plain form posts; queue health monitor with per-queue traffic-light status (see Observability); automated staleness detection and refresh with an explicit "data is out of date, refreshing" hint.  Mobile push deferred (D-0015).  Holds router credentials only for step-up-gated management probes; holds no Docker socket or host execution rights.
`viegard-host-agent` | systemd service on Docker host | Privileged host-side upgrade agent.  Polls fixed-verb `host_upgrade_commands` through the adjacent PostgreSQL container, claims commands for target `vm`, and runs only `deploy/viegard-deploy.sh upgrade --yes`.
`viegard-pipeline` | Worker Service (Generic Host) | Role-configurable host binary; deployable one or more times, each instance running a configured subset of pipeline modules (ingestion, normalization, correlation, classification, policy, actions, ban reconciliation, maintenance retention, ingestion-filter seeding, audit).  Holds only the credentials its configured modules need.  No inbound listener except a bind-local health endpoint.
`viegard-admin` | ASP.NET Core (Blazor Web App: static SSR, D-0016) | Mobile-compatible admin GUI + API: local-account authentication with mandatory TOTP and WebAuthn security keys (D-0032), server-side filtered and sortable read access to incidents, classifications, decisions, and audit; decision-list trigger summaries plus inline approve/reject actions that reuse the audited review flow and return the operator to the decisions list, anchored to the next still-reviewable row with the reviewed row rebadged approved or rejected, for rapid sequential review; step-up-gated runtime configuration editors for custom signatures, retention periods, ingestion filters, satellite database roles with one-time password display, and MikroTik router registry entries with encrypted credentials and probes; custom-signature previews that run the same literal matcher as the pipeline against a bounded recent-event scan without writing audit or config rows; command submission (approve/reject action, unblock IP, reclassify, retry, corrections) usable from a phone, degradable to plain form posts; queue health monitor with per-queue traffic-light status (see Observability); automated staleness detection and refresh with an explicit "data is out of date, refreshing" hint.  Mobile push deferred (D-0015).  Holds router credentials only for step-up-gated management probes.
Ollama | Existing/third-party | Optional local inference endpoint for the advisory classifier.  Disabled by default; endpoint and model are runtime settings.
Database | PostgreSQL 17 container (D-0024) | Shared persistence for events, incidents, classifications, decisions, actions, active bans, audit, commands, host upgrade requests, feedback, telemetry, instance version registry, MikroTik router registry, and durable queues (`SKIP LOCKED` + `LISTEN/NOTIFY`); nightly `pg_dump` sidecar for DR

### Host roles and process topology (proposal)

The pipeline host executable is **role-configurable**: its configuration declares which modules the instance runs.  This makes the number of processes extensible per service without code changes.  Examples:

- v1 default: one instance running every module.
- Later: one instance per mail provider (each holding only that provider's credentials), one instance for nginx ingestion, one core instance for correlation + policy + actions, and one maintenance instance for retention.

Rules:

- **Singleton roles.**  The correlator (single writer over incident state), policy/action engine (guardrail counters, action rate caps, circuit breaker state must be globally consistent), and maintenance retention worker run in exactly one instance.  Configuration validation rejects topologies that violate this.
- **Multi-instance roles.**  Data-source and classification modules fan out freely.  Each configured data-source instance (e.g., each IMAP account) is an isolated worker with its own connection, credential, ingestion offsets, and health contributor, regardless of which process hosts it.
- **Transport follows topology.**  Modules co-located in one process communicate over in-process bounded channels; modules split across processes use a durable queue implementation of the same `IWorkQueue` port (database-backed table queue first; an external broker can replace it later, see TODO).  Module code is identical in both topologies.

### Inter-service communication (proposal)

The admin API never calls into the pipeline process.  It reads shared persistence directly and writes **commands** (e.g., `ApproveAction`, `UnblockIp`, `RetryClassification`) to a persisted command table/queue.  The pipeline host polls/subscribes, validates each command against policy, executes, and audits.  Host upgrades use a separate fixed-verb table consumed by the root-owned `viegard-host-agent`, so the admin container still has no Docker socket or host execution rights.  Benefits: the pipeline exposes no attack surface, commands are durable and auditable, and manual-approval mode falls out naturally.  Trade-off: command execution is asynchronous (typically sub-second at single-operator scale).

## Solution layout (proposed)

```
Viegard.slnx                   Solution (XML solution format; .NET 10 SDK default)
Directory.Build.props          NuGetAudit, nullable, warnings-as-errors, LangVersion
src/
  Viegard.Domain/              Entities, value objects, enums, admin preferences;
                               zero external dependencies
  Viegard.Application/         Ports (interfaces), deterministic classification,
                               pipeline orchestration, policy engine, prompt assembly,
                               schema validation, guardrails, admin auth helpers,
                               router credential protection, router transport helpers, and
                               admin list filter helpers
  Viegard.Persistence/         Store implementations (in-memory/file first; DB when chosen),
                               including router registry and active-ban stores
  Viegard.Sources.Imap/        IMAP data source adapter (MailKit)
  Viegard.Sources.Syslog/      Syslog UDP source adapter; nginx/SWAG access logs normalize
                               to HTTP request events, other tags remain generic syslog
  Viegard.Sources.MDaemonLogs/ MDaemon flat-file log source adapter; SMTP/IMAP/POP session
                               transcripts, Screening, and Dynamic Screening logs normalize
                               to MDaemon credential-attack and IP-block evidence
  Viegard.Inference.Ollama/    Ollama adapter for the optional local-model advisor.  HTTP
                               wire protocol lives here only; never in Domain/Application.
  Viegard.Actions.Imap/        Email action provider
  Viegard.Actions.MikroTik/    RouterOS address-list action provider
  Viegard.Actions.Fail2Ban/    Fail2Ban integration (mode TBD)
  Viegard.Notifications.Email/ Operator status/alert emails via SMTP (MailKit)
  Viegard.PipelineHost/        Worker service executable, including ingestion,
                               correlation, classification, policy, actions,
                               ban reconciliation, and maintenance retention and
                               ingestion-filter workers, plus advisor consult
                               persistence diagnostics
  Viegard.AdminApi/            Admin API executable, auth endpoints, WebAuthn adapter,
                               static SSR pages, configuration editors, router probes, display preferences,
                               Advisor dashboard, keyset pagination, filter, and sort UI, first-party WebAuthn JS bridge
tests/
  Viegard.AdminApi.Tests/      Admin API adapter, WebAuthn option, display, and
                               pagination tests
  Viegard.Domain.Tests/
  Viegard.Application.Tests/   Policy, guardrails, deterministic classification,
                               schema validation, prompt injection, list filtering
  Viegard.Sources.Imap.Tests/
  Viegard.Sources.Syslog.Tests/       Syslog and nginx parser tests
  Viegard.Sources.MDaemonLogs.Tests/  Sanitized MDaemon parser fixtures
  Viegard.Integration.Tests/   Inference, ingestion, action providers (no real credentials)
  fixtures/                    nginx log corpora, representative emails, malformed AI output
docs/
deploy/                        Dockerfiles, sanitized compose examples, Linux deploy
                               script with deployed-commit tracking, host upgrade
                               agent unit/script, and Windows MDaemon satellite
                               installer
tools/
  ui-check/                    Playwright screenshot harness: walks every admin page
                               at phone and desktop viewports against a local
                               throwaway instance (see tools/ui-check/README.md)
```

Adapters are separate projects so integrations stay optional, independently testable, and additive: new sources/actions never modify the core.  Project count is higher, but each project is small.

Implemented source integrations:

- IMAP mail source, using per-account configuration and read-only folder access.
- Syslog UDP source, with source allowlist, size cap, rate cap, RFC 3164/5424 parsing, and nginx access-log normalization.
- MDaemon flat-file log source, intended for the Windows satellite pipeline instance on the MDaemon host.  It tails configured per-day log patterns, stores byte offsets per file, baselines existing files by default, skips session-log banners, drops Dynamic Screening noise by default, and normalizes SMTP/IMAP/POP, Screening, and Dynamic Screening lines into shared IP-correlatable events.  Runtime ingestion filters can suppress configured low-value MDaemon kinds before event storage and queueing while raw observations still persist every payload.  The Windows satellite installer publishes the pipeline host as the client-specific `ViegardSatelliteMDaemon` service with only the `sources` role enabled.

### Deterministic classifier tunables

`DeterministicIncidentClassifier` derives severity from the summed evidence score (`SeverityPerScorePoint`) and confidence from the score relative to `ScoreForFullConfidence`, plus a bounded repeat-volume term.  The repeat term adds `min(RepeatConfidenceBonusCap, RepeatConfidenceCoefficient * log2(eventCount))` to confidence only when an incident's member-event count reaches `RepeatConfidenceMinEvents`, so a high-volume incident (for example a repeated login brute force whose events collapse to one evidence item) can raise confidence toward the action gate without its severity changing; severity stays decoupled from the repeat term.  The six controls (`ScoreForFullConfidence`, `SeverityPerScorePoint`, `BlockRecommendationScore`, `RepeatConfidenceMinEvents`, `RepeatConfidenceCoefficient`, `RepeatConfidenceBonusCap`) are database-owned in the single-row `classifier_settings` table, seeded create-only from `ClassifierOptions`, edited on `/configuration` through the step-up-gated audited write, hot-refreshed on the `viegard_config_classifier_settings` NOTIFY channel with a code-options fallback until seeded, and exposed read-only at `/api/v1/classifier/settings`.  The parallel policy thresholds row is exposed at `/api/v1/policy/thresholds`.

### Rate-based burst detection (propose-only)

Single-event detection rules (`IDetectionRule`) cannot count, so repeated-occurrence signals are handled by a separate aggregate abstraction.  `BurstDetector` maintains a per-(signal, source) sliding-window count through `IBurstWindowStore` and, when a signal's count reaches its threshold within the window, emits a `BurstFiring`.  It is driven from `CorrelationWorker`, the single stage every ingested event already passes through, so no new pipeline queue is added.  A firing becomes an Incident with a distinct correlation key `burst:{signal}:{sourceKey}` (so it never merges with the per-IP incidents built by `TimeWindowCorrelator`), carrying the contributing event ids from the window and one evidence item scored `min(3.0 + 0.2 * (count - threshold), 5.0)`; the proposal then flows through the existing classification, policy, and decision path to `/decisions`.  Because the evidence score maps to severity through the classifier's `SeverityPerScorePoint`, retuning the classifier tunables shifts the effective severity of a burst proposal.

The reference signal is admin authentication failures (`AuthFailureBurstSignal`): `AdminAuthEvent` kinds LoginFailed, TotpFailed, StepUpFailed, and WebAuthnFailed, keyed by source IP (`ip=` plus the correlator's primary IP, falling back to the remote address); LockoutTriggered is excluded as a downstream aggregate.  Window and cooldown state are durable in PostgreSQL from the outset (`burst_windows` for occurrences, pruned to the window on every record; `burst_cooldowns` for the per-(signal, source) last-fired timestamp, updated with an atomic `ON CONFLICT` upsert gated on the cooldown interval), so counts survive restarts and concurrent instances cannot both fire a proposal.  The six controls (`GlobalEnabled`, `AuthFailureEnabled`, `AuthFailureThreshold`, `AuthFailureWindowSeconds`, `AuthFailureCooldownSeconds`, `AuthFailureActionEligible`) are database-owned in the single-row `burst_detection_settings` table, seeded create-only from `BurstDetectionOptions`, edited on `/configuration#burst-detection` through the step-up-gated audited write, hot-refreshed on the `viegard_config_burst_detection_settings` NOTIFY channel with a code-options fallback until seeded, and exposed read-only at `/api/v1/burst/settings`.  The detector is propose-only: `AuthFailureActionEligible` defaults false and the system runs in dry-run posture, so a firing is a review proposal, never an unattended action.

### DNS-over-HTTPS (DoH) server blocklist

The actions role keeps the pre-existing `dns_over_https_servers` MikroTik address list current on every managed router so the firewall can block DoH resolvers that bypass local DNS controls (D-0054).  Three stages run on their own intervals.  `DohFeedFetchWorker` downloads curated DoH IPv4 feeds (dibdot `doh-ipv4.txt` default primary, an optional secondary), parses plain-text one-address-per-line bodies through `DohFeedParser` keeping only IPv4 addresses and IPv4 CIDR ranges, merges and de-duplicates them, and replaces the `doh_desired_addresses` snapshot; the feeds are the sole authority for inclusion.  `DohProbeWorker` sends a canary DoH TXT query (`DohCanaryProbe`, DNS wire-format per RFC 8484) to each candidate over HTTPS by IP for an operator-owned FQDN (default `_doh_canary.example.com`) and records a `doh_probe_results` row classified Confirmed (HTTP 200 + `application/dns-message` + the published token present in the body), RespondedNonCompliant, Refused, or Timeout; the probe is confirmation-only and never the sole basis for removing an address, because a resolver can refuse or rate-limit the probe and still be a DoH server.  Because a resolver probed by IP frequently rejects the default POST-over-HTTP/2 request even when it is compliant (observed as HTTP 505, 400/404/405, or a dropped connection), the probe walks a small fallback ladder per address - POST then GET, each over HTTP/2 then HTTP/1.1 - and reports Confirmed as soon as any variant returns the canary token, otherwise the most informative non-confirming outcome (D-0056); the ladder short-circuits once an address returns no HTTP response twice, so an unreachable host does not pay the full ladder cost.  Because probing by IP presents a certificate that will not match, the probe HTTP client tolerates certificate mismatch, which exposes no secret since the canary token is public in DNS.  `DohReconciliationWorker` full-syncs the confirmed/curated set into the owned address list on each enabled router (adds missing, removes extraneous, updates each entry's comment to annotate confirmation state via a RouterOS REST PATCH), reusing the router registry, credential protector, and pinned-TLS HTTP client factory.  The thirteen controls (master switch, primary/secondary feed URLs, address-list name, fetch interval, probe enable/FQDN/token/path/timeout/concurrency/interval, and the apply-to-routers toggle) are database-owned in the single-row `doh_blocklist_settings` table, seeded create-only from `DohBlocklistOptions`, edited on `/configuration#doh-blocklist` through the step-up-gated audited write, hot-refreshed on the `viegard_config_doh_blocklist_settings` NOTIFY channel with a code-options fallback until seeded, and exposed read-only at `/api/v1/doh/settings`.  An operator can force an immediate probe cycle with the "Probe now" button on `/configuration#doh-blocklist`, which posts to the authenticated, audited `/configuration/doh/probe-now` endpoint (D-0057); because the probe worker runs in the PipelineHost, the request crosses processes through `IDohProbeTrigger` over the `viegard_doh_probe_now` NOTIFY channel, and `DohProbeWorker` waits on that trigger instead of a plain delay so a request wakes it early while it still runs at startup and every `ProbeInterval`.  Router writes are propose-only until `ApplyToRouters` is enabled and global dry-run is off (effective dry-run = `posture.DryRun || !settings.ApplyToRouters`), so the default posture logs the add/remove/comment set without changing any router.  v1 is IPv4-only, matching the existing IPv4 `dns_over_https_servers` firewall usage.  Because the reconciliation worker runs in the PipelineHost while the Admin UI and API run in the AdminApi, the propose-only output is made visible through Postgres (D-0055): every cycle with changes records an audit entry (in dry-run as well as applied cycles, gated on the cycle's `HasChanges`) and persists the latest full proposal to the single-row `doh_reconciliation_proposal` table (`detail_json` holding the serialized `DohReconciliationProposal` with per-router add/remove/comment-update lists) through `IDohReconciliationProposalStore`, which the AdminApi reads for the read-only `/api/v1/doh/proposals` endpoint and the "Latest reconciliation proposal" panel on `/configuration#doh-blocklist`.  Alongside the proposal, `DohProbeWorker` aggregates the per-address `doh_probe_results` rows into a probe-outcome summary at each cycle end (`IDohProbeResultStore.CountByStatusAsync`, a SQL `GROUP BY`) and persists it to the single-row `doh_probe_summary` table through `IDohProbeSummaryStore`, keeping exactly the two most recent cycles (Current and Prior, rolled on save) so the AdminApi can render a "Probe outcomes" Now / Prior run / Delta table (Refused split by HTTP status) and expose the same data read-only at `/api/v1/doh/probes/summary` (D-0059).

### Incident coalescing (same-source events)

`TimeWindowCorrelator` merges same-source events that arrive close together into one incident so the operator reviews one decision.  The first event creates an incident and emits an immediate provisional decision.  The incident stays absorbable while `CoalesceUntil` (set to `OccurredAt + SettleWindowSeconds`, extended by each absorbed event, bounded by `WindowStart + MaxCoalesceWindowSeconds`) is in the future; absorbability is expressed through `CoalesceUntil` alone rather than a new incident state, and a null `CoalesceUntil` is the terminal signal that an incident neither absorbs nor is ready to finalize.  Events during the window append to the incident and extend the deadline through `FindCoalescibleByCorrelationKeyAsync` but are not enqueued for classification (`CorrelationWorker` enqueues only when `EventIds.Count == 1`), which is the debounce.  `CoalescingFinalizerWorker` polls once a second via `ListCoalescingReadyAsync`; when the deadline passes it re-opens and re-enqueues the incident for one merged decision if it grew beyond the provisional decision's `DecidedEventCount`, otherwise it closes the incident with no new decision.  On each decision, `PolicyWorker` supersedes the prior still-pending provisional decision through `TrySupersedeAsync` (setting `SupersededAt`/`SupersededByDecisionId` only when the target is still `RequireApproval`, unreviewed, and not already superseded); superseded decisions are excluded from the review queue and counts but remain listed and badged "Superseded" on `/decisions`.  A human review of the provisional decision finalizes the incident (sets `Closed`, nulls `CoalesceUntil`) so later same-source events start a fresh incident.  The three controls (`Enabled`, `SettleWindowSeconds`, `MaxCoalesceWindowSeconds`) are database-owned in the single-row `incident_coalescing_settings` table, seeded create-only from `IncidentCoalescingOptions`, edited on `/configuration#incident-coalescing` through the step-up-gated audited write, hot-refreshed on the `viegard_config_incident_coalescing_settings` NOTIFY channel with a code-options fallback until seeded, and exposed read-only at `/api/v1/coalescing/settings`.  When `Enabled` is false the correlator keeps the prior `FindOpenByCorrelationKeyAsync` plus sliding-window reach behaviour and `CoalesceUntil` stays null, so the finalizer never touches those incidents.

### Local-model advisor

The local-model advisor is optional, disabled by default, and database-owned through `/configuration#local-model-advisor` plus `local_model_advisor_settings`.  The settings row includes the Ollama endpoint, model, timeout, keep-alive, temperature, confidence invocation band, severity clamp, confidence clamp, version, seed timestamp, update timestamp, and updater.  The maintenance role seeds the row from bootstrap options, and classification-role instances hot-refresh a last-known-good snapshot through LISTEN/NOTIFY with polling fallback.

`AdvisoryIncidentClassifier` decorates `DeterministicIncidentClassifier` and is the only registered `IClassifier` in the classification role.  The pipeline therefore still persists one `Classification` and one downstream `Decision` per incident.  The decorator preserves the deterministic classifier id, category, and recommended action.  A valid model response can only raise severity and confidence, and only up to `MaxSeverityDelta` and `MaxConfidenceDelta`; code enforces this after schema validation, so prompt injection cannot lower the base classification.

Prompt safety uses the existing `PromptAssembler`.  Deterministic context is supplied as trusted application variables, while evidence descriptions and attacker-controlled event fields are emitted only inside random-boundary untrusted data blocks.  `Viegard.Inference.Ollama` sends the assembled prompt as the system message, uses a fixed trusted user message, requests Ollama JSON-schema constrained output, and returns provider failures as `InferenceResult.Failure`.  Provider failures, invalid JSON, oversized responses, timeouts, and unexpected exceptions fail open to the deterministic classification.

Advisor observability is stored in `local_model_advisor_consults`.  The decorator records enabled-only skipped consults, provider failures, invalid output, escalations, no-change outcomes, base-to-final severity and confidence, latency, failure kind, model id, classification id, incident id, and creation time.  Recording is best-effort and cannot fail classification.  `/advisor` shows rolling outcome counts, escalation rate, failure counts, latency p50/p95, and recent consult rows.  Decision detail pages show the consult attached to the decision's classification and list the `[advisor]` reasons already stored on the classification.  Retention prunes consult rows after 90 days by default.

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
`IRetentionStore` | Roost | Batched deletes for configured data-retention targets; unset periods keep rows forever
`IRetentionSettingsStore` | Roost | Database-owned retention periods plus last-cycle status for admin editing and worker execution
`ILocalModelAdvisorConsultStore` | Roost | Append-only local-model advisor consult records, dashboard aggregations, decision lookup, and 90-day pruning
`IIngestionFilterStore` | Roost | Database-owned source-type/event-kind suppression matrix for normalization-time event emission, with notification-backed refresh and fail-open runtime reads
`IInstanceRegistryStore` | Roost | Latest build/version registration per running admin or pipeline instance: instance id, full informational version, commit SHA, roles, host name, start time, and report time
`IActiveBanStore` | Roost | PostgreSQL-owned desired state for active MikroTik bans.  One row per canonical IP records expiry, decision id, and action id; the actions-role reconciler converges routers to this table.
`ISatelliteRoleStore` | Roost | Lists, creates, rotates, and revokes per-satellite PostgreSQL roles behind the admin UI.  Role names use the enforced `viegard_sat_` prefix; generated passwords are shown once and are never audited.
`IMikroTikRouterStore` | Roost | Lists, creates, updates, enables, disables, and deletes UI-managed MikroTik router registry entries.  Per-router passwords are stored only as AES-256-GCM ciphertext and are never returned on router records.
`IRouterCredentialProtector` | Roost | Encrypts and decrypts per-router credentials with AES-256-GCM using `viegard-router-credentials-key`; router id AAD prevents ciphertext transplant between rows.
`IHostUpgradeCommandStore` | Roost | Fixed-verb host upgrade request port with per-target single-flight, 10-minute cooldown, recent history, atomic claim, and completion status.
`ISecretProvider` | Roost | Named secret retrieval; file-mounted (prod) and user-secrets (dev) implementations
`IHealthContributor` | - | Per-component health surfaced by both hosts
`ICommandQueue` | - | Durable admin-to-pipeline commands
`IWorkQueue` | Flight | Broker-semantics work queue port (ack/abandon, serializable messages, idempotent consumers); in-process bounded-channel implementation first
`IWebAuthnService` | - | Library-free WebAuthn ceremony port.  Fido2/Fido2.Models types are isolated to `Viegard.AdminApi` as a D-0032 supply-chain mitigation

## Event and decision model (proposed)

Common envelope, single normalized representation across sources:

Type | Key fields
-----|-----------
`RawObservation` | id, source id, observed-at, raw payload reference, ingest offset
`NormalizedEvent` | id, source type, occurred-at, entity refs (IP, email address, host, URI, user), typed payload (`HttpRequestEvent`, `MailMessageEvent`, ...), raw reference
`Incident` | id, correlation key/dimensions, time window, member event ids, evidence items with scores, state
`Classification` | id, subject (incident or message), classifier id, model + version + prompt/template version (when AI), category, confidence, severity, reasons, recommended action, uncertainty; **schema-validated before use**
`Decision` | id, classification id, policy id + version, outcome, rationale, guardrail evaluations (protected lists, rate caps, circuit breaker, dry-run, approval mode)
`ActionRecord` | id, decision id, provider, operation, parameters, per-target results, error, rollback info, timestamps
`AuditRecord` | Links the entire chain: event -> incident -> classification -> decision -> action; answers "why was this IP blocked?" / "why was this email moved?" without raw-log reconstruction
`Correction` | Human feedback (AI said X, the operator said Y), stored separately from the original classification

Typed payloads keep source-specific detail out of the shared envelope, so mail, nginx, Fail2Ban, MikroTik, and Windows events correlate without inventing incompatible representations.

## Security boundaries

Boundary | Rule
---------|-----
Untrusted data | All observed content (bodies, subjects, URLs, User-Agents, filenames, log lines) is data, never instructions.  It enters prompts only inside clearly delimited untrusted-data blocks via prompt templates; it never reaches shell, SQL, RouterOS, or file paths unescaped.
Inference | Prompt assembly separates SYSTEM / APPLICATION / UNTRUSTED-OBSERVED-DATA.  Model output is parsed against a strict schema; anything malformed, incomplete, oversized, or contradictory is discarded and recorded as an AI failure.  Local-only by default; no silent fallback to remote.
Policy | The policy engine is the only path to actions.  Guardrails (protected addresses/networks/hosts, action rate caps, max ban duration, cooldowns, circuit breaker, emergency stop, dry-run, approval mode) are enforced here and cannot be bypassed by any classifier.
Actions | Providers expose a closed catalog of typed operations (e.g., `AddAddressListEntry(ip, list, ttl)`), never command strings.  IP syntax, private/reserved ranges, and protected lists are validated at this layer too (defense in depth).  Destructive operations (mail delete, firewall change) ship disabled and require explicit configuration.  The MikroTik provider writes desired state to `active_bans`, writes timed entries only to the fixed `viegard-banned` address list, and reports per-router outcomes for retry and audit.  The actions-role reconciler fully owns that one RouterOS list and removes entries with no active-ban row; other router lists are never touched.
Admin | Separate process; local accounts with cookie authentication backed by server-side revocable sessions, mandatory TOTP, WebAuthn security keys, recovery codes, step-up verification, rate limiting, and fail-closed AllowedSources (D-0032/D-0033).  A step-up-gated POST that arrives without a fresh verification is captured once inside the shared `AdminStepUpGate` (path plus form fields, including the antiforgery token, keyed by session id in an in-memory single-use `IPendingStepUpActionStore`) and replayed after the operator completes step-up: the completion handlers redirect to `GET /auth/step-up/continue`, which re-dispatches the stored request to its POST endpoint so the gated action resumes instead of being discarded (D-0053).  Step-up validity and the resume-stash TTL are database-owned in the single-row `session_security_settings` table, seeded on admin-API startup from `SessionSecurityOptions`, edited on `/configuration#session-security` through the step-up-gated audited write, hot-refreshed on the `viegard_config_session_security_settings` NOTIFY channel with a code-options fallback until seeded, and exposed read-only at `/api/v1/session-security/settings`.  Satellite database role management is step-up-gated, audited without passwords, and displays generated passwords once via a short-lived protected cookie.  Commands are durable, validated, and audited; the admin API cannot invoke actions directly.
Secrets | `ISecretProvider` only.  Never in source, config in git, logs, prompts, exceptions, telemetry, audit records, or docs.

## Failure handling

- **AI unavailable/timeout/malformed:** classification marked failed, event/incident retained, deterministic rules keep operating, retries per configurable policy, failure visible in health status.  Never more permissive on failure.
- **Parse failures:** recorded as malformed-record events; ingestion continues.
- **Action failures:** recorded with error and rollback info; circuit breaker counts them.
- **Backpressure:** bounded queues; ingestion offsets persist so restarts resume without loss or duplication.

## Observability

Both hosts expose health endpoints (liveness + per-component readiness: IMAP connection, log ingestion, inference backend, queue depth, action providers).  Metrics (classification throughput, inference latency, action counts, failures, blocked-IP count, AI errors, policy decisions) via a mechanism to be chosen when it materially affects deployment (open question).  Structured logging via `Microsoft.Extensions.Logging` abstractions; sink/format choices deferred.

### Queue health monitor (traffic-light)

The admin API/GUI displays a `/queues` dashboard with shared Queues rows and per-instance heartbeat rows, so stalled or lagging global queues and stale reporters are immediately visible (D-0012).  Instance rows union queue telemetry with the instance registry (D-0037), so admin-only instances and newly started processes can show their build version and start age even when they do not publish queue statistics.

- **Signals per queue:** depth (absolute and vs. capacity), age of the oldest unacknowledged message (the primary timeliness signal), consumer heartbeat/liveness, throughput trend, recent poison-message count.
- **Status derivation (thresholds configurable):** green = consumers alive and oldest-message age below the amber threshold; amber = lag or depth above threshold, or recent poison messages; red = no live consumer heartbeat, oldest-message age above the red threshold, or circuit breaker open.
- **Transport-independent:** pipeline instances publish per-queue telemetry and heartbeats to shared persistence on a short interval; admin and pipeline instances upsert build/version registrations at startup and on heartbeat.  The admin API computes status from those records and treats stale telemetry itself as red (detects a dead pipeline process even when a queue is empty).  For DB-backed queues the admin API can additionally measure depth and oldest-message age directly from the queue table, independent of the producer.

## Proposed initial dependencies (each requires supply-chain review before install)

Package | Purpose | Note
--------|---------|-----
MailKit | IMAP over TLS | De facto standard .NET IMAP library
xUnit (+ built-in asserts) | Testing | Avoids FluentAssertions v8 commercial-license issue
System.Threading.Channels | Bounded in-process queues | Part of the BCL, no external dependency

Everything else (EF Core or alternative, metrics exporter, notification client) waits for the corresponding decision.

## Open questions affecting this proposal

Tracked in TODO.md; the architecture keeps them behind interfaces so implementation can start on the skeleton while they are resolved: database technology, SWAG log transport, Yahoo auth + IDLE vs. polling, MikroTik API/auth, Fail2Ban mode, admin authn/authz, notifications, retention, thresholds, protected ranges, CI/CD.
