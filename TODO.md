# Viegard TODO

Living work queue and open-question tracker.  Categories: **Needs user decision**, **Implementation work**, **Known defect**, **Deferred**, **Optional improvement**.

When Hannah answers a question, remove or update the item here and record the outcome in [DECISIONS.md](DECISIONS.md).

---

## Needs user decision

### Architecture / platform

- [x] **Database technology** - DECIDED (D-0024, 2026-08-19): PostgreSQL 17 dedicated container; EF Core + Npgsql; in-database durable queues (`SKIP LOCKED` + `LISTEN/NOTIFY`); nightly `pg_dump` sidecar; SQL Server remains a possible future second provider behind the store/queue ports.
- [ ] **Event architecture** details: if a meaningful choice arises between alternatives (e.g., event store vs. event bus, push vs. pull correlation), present options before implementing.
- [ ] **Queue/broker technology beyond in-database queues**: D-0024 implements durable queues in PostgreSQL (`SKIP LOCKED` + `LISTEN/NOTIFY`); an external broker (Kafka KIP-932 share groups, RabbitMQ, NATS JetStream, Redis Streams) remains a speculative future option only if scale ever demands it.  The queue port stays broker-ready (ARCHITECTURE.md assumption 3).
- [ ] **CI/CD**: whether to use Forgejo Actions, GitHub Actions (on the mirror), both, or neither.

### Yahoo Mail / IMAP

- [x] **Yahoo authentication method** - DECIDED (D-0019): app passwords now, per-account mechanism seam for OAuth2 later.
- [x] **IMAP polling vs. IMAP IDLE** - DECIDED (D-0020): per-account IDLE with polling fallback + safety poll.
- [x] **Default monitored folders** - DECIDED (D-0021): INBOX by default, configurable per account.  Exact per-account folder lists and provider spam-folder names still needed at configuration time.
- [ ] **Permitted email actions** and their default enablement (move, copy, mark read, flag, quarantine; delete requires separate explicit enablement).  Needed for Phase 7.
- [x] **Attachment content extraction** - DECIDED (D-0022): metadata only in Phase 4; content subsystem deferred.
- [ ] **First-run ingestion baseline**: adapter defaults to new-mail-only on first run (`IngestExistingOnFirstRun` = false); confirm or change before live use.
- [ ] **Live IMAP verification**: needs Hannah to create an app password for a test account and add the account section to user-secrets; adapter has not yet run against a real server.

### SWAG / nginx logs

- [x] **Log transport mechanism** - DECIDED (D-0023): general syslog UDP source; SWAG's nginx adds syslog `access_log`/`error_log` targets while keeping file logs as the durable record.
- [ ] **Syslog deployment values** (at configuration time): Viegard listener port, allowed source IPs, and the SWAG-side `log_format`/`access_log` directives applied to nginx config.
- [ ] **nginx log format**: Viegard recommends an extended format (see `docs/swag-syslog-setup.md` once merged); confirm or adjust when configuring SWAG.

### MDaemon logs

- [ ] **MDaemon log transport**: MDaemon has no native syslog (verified 2026-08-19 against official docs).  Recommended approach: a Viegard satellite pipeline instance on the Windows host (file-source role) once PostgreSQL persistence lands (D-0024); interim alternatives: SFTP pull (Windows built-in OpenSSH) or Fluent Bit agent to the syslog listener.  Needs Hannah's confirmation after the Postgres work.
- [ ] **Which MDaemon logs to ingest** (SMTP in/out, IMAP, POP, Dynamic Screening / security logs, ActiveSync) and their configured formats.

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

- [ ] **PostgreSQL integration verification**: run the 7 skipped integration tests (queue semantics, store round-trip) once local Docker exists (WSL reboot pending) or against the Debian VM; also verify AutoMigrate + the compose stack end-to-end.
- [ ] **Admin API Postgres wiring**: register the read-side stores and command queue in `viegard-admin` when the admin features (Phase 8) land.

- [x] **Phase 2: Architecture proposal** - APPROVED by Hannah 2026-08-18 (D-0017).
- [ ] **Phase 3: Skeleton** (nearly complete) - DONE: solution layout (`Viegard.slnx`), `Directory.Build.props` with NuGetAudit enforcement, domain event/decision/audit/health model, application ports, broker-semantics `ChannelWorkQueue` with dead-lettering, `FileSecretProvider` + `ConfigurationSecretProvider`, in-memory stores, role-validated pipeline host, admin host `/healthz`, prompt assembler with random-boundary untrusted-data blocks, strict AI classification output validator, queue telemetry publication + traffic-light evaluator (D-0012; cross-process visibility arrives with the database, D-0004), Dockerfiles + sanitized compose example, 70 passing tests.  REMAINING: verify container builds on the Debian VM (no container tooling on the dev workstation; Hannah chose to defer, 2026-08-18), CI decision, admin GUI queue page (needs shared persistence).
- [ ] **Phase 4: Data sources** - IMAP adapter DONE (live-account verification outstanding); syslog/nginx source DONE (PR pending; live SWAG configuration outstanding); MDaemon logs next (approved by Hannah 2026-08-18).
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

- [ ] **Build the viegard.com website**: public project site for Viegard (branding assets exist in `docs/branding/`).  Scope, hosting, and content to be defined with Hannah.

- [ ] Model evaluation framework (accuracy, precision/recall/F1, calibration, latency, throughput, token usage, VRAM) to compare models on the real workload.  Required eventually per requirements; scheduling TBD.
