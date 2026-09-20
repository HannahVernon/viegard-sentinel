# Viegard TODO

Living work queue and open-question tracker.  Categories: **Needs user decision**, **Implementation work**, **Known defect**, **Deferred**, **Optional improvement**.

When the project owner answers a question, remove or update the item here and record the outcome in [DECISIONS.md](DECISIONS.md).

---

## Needs user decision

### Architecture / platform

- [x] **Database technology** - DECIDED (D-0024, 2026-08-19): PostgreSQL 17 dedicated container; EF Core + Npgsql; in-database durable queues (`SKIP LOCKED` + `LISTEN/NOTIFY`); nightly `pg_dump` sidecar; SQL Server remains a possible future second provider behind the store/queue ports.
- [ ] **Event architecture** details: if a meaningful choice arises between alternatives (e.g., event store vs. event bus, push vs. pull correlation), present options before implementing.
- [ ] **Queue/broker technology beyond in-database queues**: D-0024 implements durable queues in PostgreSQL (`SKIP LOCKED` + `LISTEN/NOTIFY`); an external broker (Kafka KIP-932 share groups, RabbitMQ, NATS JetStream, Redis Streams) remains a speculative future option only if scale ever demands it.  The queue port stays broker-ready (ARCHITECTURE.md assumption 3).
- [ ] **CI/CD**: whether to use Forgejo Actions, GitHub Actions (on the mirror), both, or neither.
- [ ] **Schema-change data migration** (the project owner, 2026-08-25): changing `Viegard__Database__Schema` on an existing deployment currently starts a fresh, empty schema and strands the old data (observed live when `viegard` replaced `public`).  Design an automated, data-preserving path: likely an explicit opt-in setting (e.g., `Viegard__Database__RenameSchemaFrom`) performing `ALTER SCHEMA ... RENAME` when the old schema is dedicated to Viegard, plus fail-closed startup detection ("Viegard data found in schema X but configured schema is Y") instead of silently proceeding.  Needs design decision before implementation.

### Yahoo Mail / IMAP

- [x] **Yahoo authentication method** - DECIDED (D-0019): app passwords now, per-account mechanism seam for OAuth2 later.
- [x] **IMAP polling vs. IMAP IDLE** - DECIDED (D-0020): per-account IDLE with polling fallback + safety poll.
- [x] **Default monitored folders** - DECIDED (D-0021): INBOX by default, configurable per account.  Exact per-account folder lists and provider spam-folder names still needed at configuration time.
- [ ] **Permitted email actions** and their default enablement (move, copy, mark read, flag, quarantine; delete requires separate explicit enablement).  Needed for Phase 7.
- [x] **Attachment content extraction** - DECIDED (D-0022): metadata only in Phase 4; content subsystem deferred.
- [ ] **First-run ingestion baseline**: adapter defaults to new-mail-only on first run (`IngestExistingOnFirstRun` = false); confirm or change before live use.
- [ ] **Live IMAP verification**: needs the project owner to create an app password for a test account and add the account section to user-secrets; adapter has not yet run against a real server.

### SWAG / nginx logs

- [x] **Log transport mechanism** - DECIDED (D-0023): general syslog UDP source; SWAG's nginx adds syslog `access_log`/`error_log` targets while keeping file logs as the durable record.
- [ ] **Syslog deployment values** (at configuration time): Viegard listener port, allowed source IPs, and the SWAG-side `log_format`/`access_log` directives applied to nginx config.
- [ ] **nginx log format**: Viegard recommends an extended format (see `docs/swag-syslog-setup.md` once merged); confirm or adjust when configuring SWAG.

### MDaemon logs

- [x] **MDaemon log transport** - DECIDED (D-0025, 2026-08-20): Viegard satellite pipeline instance on the MDaemon Windows host (sources role only) writing to shared Postgres over the LAN.
- [ ] **MDaemon satellite prerequisites**: publish Postgres 5432 bound to the LAN and firewall it to the satellite hosts (DOCKER-USER rule); per-satellite least-privilege DB credentials NOW UI-MANAGED (2026-09-15: Configuration -> Satellites creates/rotates/revokes roles; tightening the v1 full-DML grant posture to table-level grants is future work).  Windows service deployment TOOLING DONE 2026-09-15: deploy/windows/viegard-satellite.ps1 (install/upgrade/status, -Client profiles, service-account choice, Server 2019+, PS 5.1).  Deployment tracking DONE 2026-09-16: upgrades compare `.deployed-commit` with clone HEAD and status reports both values.  Admin-requested satellite upgrades DONE 2026-09-16: satellites claim only their configured target, use the registered scheduled task for elevation, and reconcile completion after restart.
- [x] **Which MDaemon logs to ingest and their formats** - RESOLVED 2026-08-20 via real-log analysis and sanitized fixtures: ingest per-service MDaemon logs for SMTP in/out, IMAP, POP3, Screening, and `DynScrn-*.log` Dynamic Screening.  Prefer per-service files over the combined `-all.log` to avoid duplicate ingestion.  ActiveSync is not in the initial parser set.

### Inference

- [ ] **Dev model selection**: which small quantized Qwen-class model and quantization level for the CPU-only dev workstation.
- [ ] **Production model selection** for the V100 inference server.
- [x] **Classification thresholds** - DECIDED provisionally by D-0027, 2026-08-20: AI action at confidence >= 0.9 and severity >= 7, review at confidence >= 0.7, with deterministic evidence using the normalized confidence band.  Calibrate after dry-run deployment before enabling unattended action.

### Actions / integrations

- [x] **MikroTik RouterOS API version and authentication method** - DECIDED 2026-09-16 (D-0038 and D-0006 amendments): RouterOS 7.x REST, dedicated source-restricted router accounts with `read`, `write`, `api`, and `rest-api`, fixed `viegard-banned` address list, 1d default ban with 7d and 30d approval choices.
- [ ] **Fail2Ban integration mode**: adds entries, consumes events, manages jails, or input/output only.
- [x] **Automatic-action thresholds**, maximum ban durations, cooldowns, and escalation rules - DECIDED provisionally by D-0027, 2026-08-20: temp ban 24h, repeat offender 7d after 3 incidents in 7d, max auto ban 30d, caps 20/hour and 100/day, circuit breaker after 5 consecutive action failures or cap breach.  Calibrate after dry-run deployment.
- [x] **Protected IP ranges** - DECIDED (D-0026, 2026-08-20): default list of all RFC 1918 + CGNAT + loopback + link-local + ULA + artifact guards (IPv4 and IPv6); operator-extensible at setup and via the admin UI.  the project owner's own public statics are deployment configuration (recorded privately, never in this repo); the admin UI protected-list editor is Phase 8 work.

### Operations

- [ ] **HashiCorp Vault for secrets**: discuss adopting Vault as an `ISecretProvider` implementation (deployment cost of running a Vault container, unseal/auto-unseal workflow, audit and rotation benefits, versus mounted secret files per D-0006).
- [ ] **Operator email notification details**: sending SMTP server/account, sender/recipient addresses, TLS settings, and which events warrant email vs. push.
- [ ] **How the phone reaches the admin GUI** (VPN such as WireGuard vs. exposure through SWAG; affects Web Push subscription and admin auth threat model).
- [ ] **Syslog source trust weighting**: once many LAN hosts may send syslog (wide allowlist), forged log lines from any allowed host become an injection vector for fake incidents.  Harmless under dry-run; before Phase 7 automatic actions, consider per-source trust weighting or per-source evidence caps.
- [x] **Admin API authentication model** - DECIDED (D-0032/D-0033, 2026-08-25): local accounts, server-side revocable sessions, mandatory TOTP and WebAuthn security keys, recovery codes, step-up, fail-closed AllowedSources, and explicit loopback/direct/proxy exposure modes.
- [ ] **Device Bound Session Credentials tracking**: DBSC remains future work once browser support and the standard stabilize.  The Phase 8 session registry is the substrate a later DBSC binding can plug into.
- [x] **Retention periods** - DECIDED 2026-09-14 (D-0035): raw observations 30d, events 90d, decision chain 180d, audit 365d, dead-lettered queue messages 30d, expired admin sessions 30d past expiry; corrections kept forever.  Enforced by the in-app retention worker (singleton `maintenance` role); unset periods keep rows forever.  Periods are seeded once from env and thereafter owned/edited on the admin /configuration page (second D-0029 slice).  Model prompts/responses get a period when Phase 6 introduces them.
- [ ] **Observability/monitoring technology** if the choice materially affects deployment.
- [ ] **Queue traffic-light thresholds**: amber/red values for oldest-message age, depth, and heartbeat staleness per queue (see D-0012); defaults need the project owner's approval.
- [ ] **Forgejo branch protection** for `main` and `dev` (GitHub rulesets are applied on the mirror; decide whether to mirror the protection on Forgejo).

## Implementation work

- [ ] **Operational config store (D-0029)**: FIRST SLICE DONE 2026-09-14 (detection signatures: `custom_signatures` store, LISTEN/NOTIFY hot refresh, step-up-gated audited edits at /signatures, aftership rule seeded at severity 3).  POLICY THRESHOLDS DONE 2026-09-16 (`policy_threshold_settings`, seed-once from env, /configuration editing, LISTEN/NOTIFY hot refresh).  REMAINING slices: allow/deny lists, protected-range editor (D-0026), posture flags: each needs its own review since posture writes change what the system may do.
- [ ] **Reference-table follow-ons (D-0031)**: normalize `classifications.category`/`recommended_action` and `actions.operation_id` once the D-0029 config store defines those vocabularies; move `corrections.corrected_by` to the users table when admin auth lands.
- [ ] **Security audit remediation (2026-08-25)**: codebase evaluated against all 25 applicable prompts from `ai-security-audit` (commit 5885e32; 07/15/28 N/A - no installer, PowerShell, or CI/CD).  No Critical/High findings.  Medium fixes (structured allowlist sender, IMAP body fetch cap, MDaemon symlink rejection, pinned nuget.config) and low fixes (bounded tail reads, UID-wrap guard, log sanitization, stats cast, Hosting patch) tracked via PRs.  REMAINING - revisit deferred recommendations at Phase 9 hardening: exception text persisted in audit records (keep vs generic code + DetailJson), NOTIFY channel spam (mitigate via least-privilege per-instance DB roles with the D-0025 satellite), secret string zeroization (managed-memory limits; MailKit/Npgsql APIs take strings), in-memory audit ledger dev-only doc note.

- [x] **PostgreSQL integration verification** - DONE 2026-08-20: all 7 integration tests pass against a live postgres:17 container (Docker CE in WSL2 Debian on the dev workstation).  Three defects found and fixed by the tests: PascalCase/snake_case column mismatch vs. the queue's raw SQL, missing dead_lettered value on enqueue, unsupported FULL JOIN in the stats query, plus jsonb key-reordering breaking the polymorphic discriminator (fixed with AllowOutOfOrderMetadataProperties).  Remaining: verify the compose stack itself on the Debian VM at deployment time.
- [x] **Admin API Postgres wiring** - DONE in Phase 8 increment 1: `viegard-admin` now selects the same `inmemory` or `postgres` persistence providers as the pipeline host.
- [x] **Instance version registry and deployed-commit tracking** - DONE 2026-09-16 (D-0037): admin and pipeline instances report full informational version, commit SHA, roles, host name, start time, and report time to `instance_registry`; `/queues` shows instance version/start age; Linux and Windows deploy upgrades compare the deployed marker to clone HEAD before skipping rebuilds.
- [x] **Operational hygiene batch** - DONE 2026-09-17: list-page filter indexes and `pg_trgm` support for large event/audit searches, raw-observation replay inserts through `ON CONFLICT DO NOTHING`, uncharged queue lease release on graceful shutdown, queue totals legend, operator-facing decision outcome vocabulary, upgrade target discovery from the instance registry, satellite upgrade state under a writable per-target ProgramData directory, honest satellite post-start verification based on Running plus no new Error-level Application events, per-instance MDaemon offset and payload-reference keying, and Docker restore-cache layering for admin and pipeline images.

- [x] **Phase 2: Architecture proposal** - APPROVED by the project owner 2026-08-18 (D-0017).
- [ ] **Phase 3: Skeleton** (nearly complete) - DONE: solution layout (`Viegard.slnx`), `Directory.Build.props` with NuGetAudit enforcement, domain event/decision/audit/health model, application ports, broker-semantics `ChannelWorkQueue` with dead-lettering, `FileSecretProvider` + `ConfigurationSecretProvider`, in-memory stores, role-validated pipeline host, admin host `/healthz`, prompt assembler with random-boundary untrusted-data blocks, strict AI classification output validator, queue telemetry publication + traffic-light evaluator (D-0012; cross-process visibility arrives with the database, D-0004), Dockerfiles + sanitized compose example, 70 passing tests.  REMAINING: verify container builds on the Debian VM (no container tooling on the dev workstation; the project owner chose to defer, 2026-08-18), CI decision, admin GUI queue page (needs shared persistence).
- [ ] **Phase 4: Data sources** - IMAP adapter DONE (live-account verification outstanding); syslog/nginx source DONE (live SWAG configuration outstanding); MDaemon log source DONE in code with sanitized parser fixtures.  REMAINING: live MDaemon satellite deployment and Windows service configuration.
- [x] **Phase 5: Deterministic analysis and policy** - DONE 2026-08-20: deterministic HTTP/mail/MDaemon rules, time-window correlation, correlation worker, deterministic evidence classifier, classification worker, D-0027 policy engine, policy worker, protected-address guardrail, in-memory guardrail state, and provisional thresholds are implemented.  Remaining follow-up work is tracked separately below.
- [ ] **PostgreSQL guardrail-state store**: replace the current in-memory guardrail state with durable shared PostgreSQL state before unattended policy/action instances are split across processes or hosts.
- [x] **Classification stage to feed PolicyWorker** - DONE 2026-08-20: deterministic incident classification now consumes the incidents queue, persists `Classification`, enqueues the classifications queue, and `PolicyWorker` persists dry-run `Decision` records.  LLM enrichment remains optional Phase 6 work.
- [ ] **Policy threshold calibration after deployment**: review dry-run decisions against real traffic, then adjust provisional D-0027 thresholds and durations before any unattended action is approved.
- [ ] **Phase 6: Local AI** - optional inference enrichment, llama.cpp adapter, local dev inference install.
- [ ] **Phase 7: Actions** - DECIDED 2026-09-16 (D-0038): increment one is manual-approval network enforcement.  Build order:
  - [x] **Policy thresholds /configuration slice** - DONE 2026-09-16: review severity/confidence and unattended confidence seeded from env, UI-edited, LISTEN/NOTIFY to the policy engine (D-0029 slice).
  - [x] **Router registry /configuration slice** - DONE 2026-09-16: UI-managed MikroTik router list, AES-256-GCM encrypted credentials (D-0006 amendment), per-router transport mode (HTTP / HTTPS any-cert / HTTPS pinned), certificate fetch-and-pin workflow, connectivity test.
  - [x] **MikroTik action provider** - DONE 2026-09-16: RouterOS REST API, `viegard-banned` address-list entries with timeouts only (never firewall rules), fan-out to all enabled routers, per-router results with automatic retry, D-0026 protected-range guard inside the provider, dry-run mode logging exact calls first.
  - [x] **Ban persistence and reconciliation** - DONE 2026-09-16: `active_bans` is the PostgreSQL source of truth; actions-role reconciliation converges each enabled router's owned `viegard-banned` list every 5 minutes by default and re-applies reboot-cleared entries with remaining time.
  - [x] **Approval UI** - DONE 2026-09-16: approve/reject on RequireApproval decisions with 1d/7d/30d duration choice, `/bans` active-ban surface with unban, step-up gated, fully audited, and dry-run posture surfaced before approval.
  - [ ] **Crawler verification**: forward-confirmed rDNS against known crawler domains; verified crawlers never proposed for bans.
  - [ ] **Rate-based burst detection (propose-only)**: proposals for calibration; action tier deliberately absent until the unattended tier is enabled.
- [ ] **JetPack allowlist management** - DECIDED 2026-09-19 (D-0041): extend router management beyond bans. Build order:
  - [ ] **JetPack feed settings /configuration slice**: feed URL, hourly fetch interval, enabled flag, address-list name (`jetpack_servers`), seeded/database-owned per the D-0029 pattern.
  - [ ] **JetPackFeedFetchWorker**: hourly fetch of the JetPack IP feed into a `jetpack_desired_addresses` table.
  - [ ] **JetPackReconciliationWorker**: 5-minute convergence of each enabled router's `jetpack_servers` list to the desired-state table, structurally mirroring `BanReconciliationWorker` (no per-entry expiry).
  - [ ] **Retire legacy script**: disable `manage-jetpack-address-list.ps1`'s scheduled job once verified working on gr1/gr2/gr3.
  - [ ] **DoH blocklist management** (deferred, separate decision needed): current source `crypt0rr/public-doh-servers` is maintained but only every few months; needs a currency/sourcing decision before design.
- [ ] **Phase 8: Administration** - IN PROGRESS: auth (increment 1), WebAuthn (increment 2), read-only views + signature editing (increment 3, 2026-09-14: /queues /incidents /decisions /events /audit /signatures with keyset pagination, server-side list filters, and sortable headings), per-user display preferences, total-count pagination indicators, and instance version visibility are live.  REMAINING: per-service UIDs, Data Protection key encryption at rest, further D-0029 slices; in-process ACME deferred per D-0034 to a future admin-UI domain/certificate management slice.
  - [x] **Increment 1: admin authentication foundation** - local bootstrap user, password change, first-party TOTP, recovery codes, cookie auth with server-side sessions, IP binding, AllowedSources, exposure/TLS guardrails, auth auditing, and auth-failure pipeline events.
  - [x] **Increment 2: WebAuthn/FIDO2** - DONE 2026-09-08: Fido2 4.0.1 and Fido2.Models 4.0.1 are pinned in AdminApi only, behind `IWebAuthnService`; hardware-key enrollment, sign-in, step-up, deletion, persistence, audit events, docs, and tests are implemented.
  - [x] **Increment 3: ACME certificate automation** - DONE 2026-09-14 per D-0034: host certbot + deploy hook (documented in docs/deployment.md; verified end-to-end on the live deployment with `certbot renew --dry-run --run-deploy-hooks`).  In-process ACME (LettuceEncrypt/Certes) deferred to a future admin-UI domain/certificate management slice; library supply-chain review happens then.
- [ ] **Phase 9: Hardening** - security, dependency, prompt-injection, authorization reviews; failure-mode, rollback, and load testing.

## Known defect

- (none)

## Deferred

- [ ] **Full domain-name change support** (requested by the project owner 2026-09-14, alongside D-0034): make moving the admin's public domain a supported operation, eventually configurable from the admin UI (domain names for ACME validation/issuance).  Scope when picked up: TLS certificate re-issuance (the deferred in-process ACME slice), WebAuthn relying-party ID change with a lockout-safe key re-enrollment flow, cookie/session domain, AllowedSources/origins, deployment configuration, DNS cutover, and the certbot deploy-hook path (until in-process ACME exists).  Interim deliverable: a documented runbook.
- [ ] **Mobile push notification technology** (deferred by the project owner 2026-08-18, D-0015): further consideration of privacy implications needed.  Browser Web Push transits third-party relays (FCM/Apple/Mozilla) with E2E-encrypted payloads but cloud-visible delivery metadata; self-hosted alternatives (ntfy/UnifiedPush) exist.  The notification port stays pluggable for whichever mechanism is chosen.

- [ ] Additional data sources (Windows Event Log, Docker logs, SSH logs, SQL Server logs, MikroTik logs, application logs) until explicitly approved.
- [ ] Model retraining/fine-tuning workflows (feedback data is collected, but training requires explicit approval).

## Optional improvement

- [ ] **Build the viegard.com website**: public project site for Viegard (branding assets exist in `docs/branding/`).  Scope, hosting, and content to be defined with the project owner.

- [ ] Model evaluation framework (accuracy, precision/recall/F1, calibration, latency, throughput, token usage, VRAM) to compare models on the real workload.  Required eventually per requirements; scheduling TBD.
