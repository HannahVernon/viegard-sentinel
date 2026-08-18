# Viegard TODO

Living work queue and open-question tracker.  Categories: **Needs user decision**, **Implementation work**, **Known defect**, **Deferred**, **Optional improvement**.

When Hannah answers a question, remove or update the item here and record the outcome in [DECISIONS.md](DECISIONS.md).

---

## Needs user decision

### Architecture / platform

- [ ] **Database technology** for events, incidents, classifications, and audit records.  Deferred by Hannah on 2026-08-18; persistence stays behind store interfaces until chosen.  Options: SQLite (agent recommendation), PostgreSQL container, SQL Server on Linux.
- [ ] **Event architecture** details: if a meaningful choice arises between alternatives (e.g., event store vs. event bus, push vs. pull correlation), present options before implementing.
- [ ] **Queue/broker technology**, if/when the in-process channels are outgrown.  The queue port is broker-ready by design (see ARCHITECTURE.md assumption 3).  Research needed: compare candidates on durability, ordering, ack/poison semantics, .NET client quality, and operational cost on a single Docker host.  Candidates raised so far: SQL Server Service Broker (natural fit only if the database decision lands on SQL Server), Apache Kafka including KIP-932 "Queues for Kafka" share groups.  Others to evaluate: RabbitMQ, NATS JetStream, Redis Streams, Postgres `SKIP LOCKED` table queues.
- [ ] **CI/CD**: whether to use Forgejo Actions, GitHub Actions (on the mirror), both, or neither.

### Yahoo Mail / IMAP

- [ ] **Yahoo authentication method** (app password vs. OAuth2).
- [ ] **IMAP polling vs. IMAP IDLE** (or both, configurable).
- [ ] **Exact mailbox/folder names** to monitor and the configured spam folder name.
- [ ] **Permitted email actions** and their default enablement (move, copy, mark read, flag, quarantine; delete requires separate explicit enablement).
- [ ] **Attachment content extraction**: whether the separately controlled subsystem is in scope initially.

### SWAG / nginx logs

- [ ] **Log transport mechanism** from the SWAG container (on the MikroTik container host) to the Viegard host: network bind mount (NFS/SMB), syslog forwarding, log shipper, or another mechanism.
- [ ] **Log file paths and formats** (access log format string, error log handling, custom formats).

### MDaemon logs

- [ ] **Which MDaemon logs to ingest** (SMTP in/out, IMAP, POP, Dynamic Screening / security logs, ActiveSync) and their configured formats.
- [ ] **Log transport mechanism** from the MDaemon server to the Viegard host (network share, syslog, shipper, or another mechanism), and log file paths/rotation behavior.

### Inference

- [ ] **Dev model selection**: which small quantized Qwen-class model and quantization level for the CPU-only dev workstation.
- [ ] **Production model selection** for the V100 inference server.
- [ ] **Classification thresholds** (confidence/severity) for email and security classification.

### Actions / integrations

- [ ] **MikroTik RouterOS API version and authentication method**; address-list names; expiration/timeout defaults.
- [ ] **Fail2Ban integration mode**: adds entries, consumes events, manages jails, or input/output only.
- [ ] **Automatic-action thresholds**, maximum ban durations, cooldowns, and escalation rules.
- [ ] **Protected IP ranges, trusted networks, management addresses, and protected hosts** (never auto-blocked; must be configured by Hannah, never guessed).

### Operations

- [ ] **HashiCorp Vault for secrets**: discuss adopting Vault as an `ISecretProvider` implementation (deployment cost of running a Vault container, unseal/auto-unseal workflow, audit and rotation benefits, versus mounted secret files per D-0006).
- [ ] **Operator email notification details**: sending SMTP server/account, sender/recipient addresses, TLS settings, and which events warrant email vs. push.
- [ ] **How the phone reaches the admin GUI** (VPN such as WireGuard vs. exposure through SWAG; affects Web Push subscription and admin auth threat model).
- [ ] **Admin API authentication model.**
- [ ] **Retention periods** for raw events, normalized events, incidents, classifications, actions, audit records, and model prompts/responses.
- [ ] **Observability/monitoring technology** if the choice materially affects deployment.
- [ ] **Queue traffic-light thresholds**: amber/red values for oldest-message age, depth, and heartbeat staleness per queue (see D-0012); defaults need Hannah's approval.
- [ ] **Forgejo branch protection** for `main` and `dev` (GitHub rulesets are applied on the mirror; decide whether to mirror the protection on Forgejo).

## Implementation work

- [x] **Phase 2: Architecture proposal** - APPROVED by Hannah 2026-08-18 (D-0017).
- [ ] **Phase 3: Skeleton** (in progress) - DONE: solution layout (`Viegard.slnx`), `Directory.Build.props` with NuGetAudit enforcement, domain event/decision/audit/health model, application ports, broker-semantics `ChannelWorkQueue` with dead-lettering, `FileSecretProvider` + `ConfigurationSecretProvider`, in-memory stores, role-validated pipeline host, admin host `/healthz`, 33 passing tests.  REMAINING: queue telemetry publication (D-0012 wiring), health aggregation endpoints, prompt template assembler + AI output schema validation, Dockerfiles/compose examples, CI decision.
- [ ] **Phase 4: Data sources** - IMAP, then nginx/SWAG, then MDaemon logs (approved by Hannah 2026-08-18; ordering assumed, confirm if different).
- [ ] **Phase 5: Deterministic analysis** - rules, correlation, policy evaluation.
- [ ] **Phase 6: Local AI** - inference abstraction, llama.cpp adapter, local dev inference install.
- [ ] **Phase 7: Actions** - action providers, dry-run first; real actions only after explicit approval.
- [ ] **Phase 8: Administration** - admin interface/API.
- [ ] **Phase 9: Hardening** - security, dependency, prompt-injection, authorization reviews; failure-mode, rollback, and load testing.

## Known defect

- (none)

## Deferred

- [ ] **Mobile push notification technology** (deferred by Hannah 2026-08-18, D-0015): further consideration of privacy implications needed.  Browser Web Push transits third-party relays (FCM/Apple/Mozilla) with E2E-encrypted payloads but cloud-visible delivery metadata; self-hosted alternatives (ntfy/UnifiedPush) exist.  The notification port stays pluggable for whichever mechanism is chosen.

- [ ] Additional data sources (Windows Event Log, Docker logs, SSH logs, SQL Server logs, MikroTik logs, application logs) until explicitly approved.
- [ ] Model retraining/fine-tuning workflows (feedback data is collected, but training requires explicit approval).

## Optional improvement

- [ ] Model evaluation framework (accuracy, precision/recall/F1, calibration, latency, throughput, token usage, VRAM) to compare models on the real workload.  Required eventually per requirements; scheduling TBD.
