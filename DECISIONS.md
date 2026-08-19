# Viegard Architectural Decision Record

This is a living record of consequential decisions for the Viegard project.  Each entry records the decision, when it was made, the alternatives considered, the rationale, notable consequences, and whether Hannah explicitly approved it or it is an implementation detail chosen by the coding agent.

Decisions are never rewritten.  If a later change invalidates an earlier decision, a new entry records what changed and why.

---

## D-0001: Language and runtime: .NET 10 (LTS) with modern C#

- **Date:** 2026-08-18 (Phase 1: Discovery)
- **Decision:** Build Viegard as a .NET 10 LTS application using modern C#, with a worker/service-oriented architecture and an optional ASP.NET Core administrative API.  The core domain model stays independent of infrastructure concerns.
- **Alternatives considered:** Python 3.12+, Go, Rust.
- **Rationale:** Strong async and hosting primitives (Generic Host, Channels), mature IMAP tooling (MailKit), first-class Linux container support, and alignment with Hannah's existing .NET ecosystem and standards.
- **Consequences:** NuGetAudit enforcement applies (see D-0002 when tooling is added).  All components use .NET idioms for dependency injection, configuration, and hosting.
- **Approval:** Explicitly approved by Hannah.

## D-0002: Inference abstraction is Viegard-owned and provider-neutral

- **Date:** 2026-08-18 (Phase 1: Discovery)
- **Decision:** Inference sits behind a Viegard-owned, provider-neutral `IInferenceProvider` abstraction.  OpenAI-compatible HTTP is treated as one supported interoperability protocol, not as the application's inference abstraction.  Provider adapters allow llama.cpp, vLLM, Ollama, or native adapters for non-OpenAI-compatible APIs without changing core application code.  OpenAI-specific request/response types must never appear in the domain or core application layers.
- **Alternatives considered:** Coupling directly to an OpenAI-compatible client library; coupling to a specific runtime SDK.
- **Rationale:** The application must not be tightly coupled to any model or runtime.  Backends will change (dev laptop CPU model vs. production V100 server) and future adapters must be additive.
- **Consequences:** A translation layer maps domain-level inference requests/responses to each adapter's wire protocol.
- **Approval:** Explicitly approved by Hannah.

## D-0003: Deployment: Docker container on a Debian 13 VM; core stays deployment-independent

- **Date:** 2026-08-18 (Phase 1: Discovery)
- **Decision:** Viegard runs in its own Docker container on a Debian 13 VM on Hannah's home network.  SWAG runs as a container on a separate MikroTik RouterOS host using MikroTik's container feature, so SWAG log access is cross-host (bind mount, network share, or another mechanism, to be decided).  The application core must not make Docker-specific assumptions.
- **Alternatives considered:** Bare systemd service on the SWAG host; Windows service; Kubernetes.
- **Rationale:** Hannah's stated deployment environment.  Deployment independence keeps the core portable.
- **Consequences:** Log ingestion must tolerate whatever transport delivers SWAG logs to the Viegard host.  The exact mechanism is an open question (see TODO).
- **Approval:** Explicitly approved by Hannah.

## D-0004: Database choice deferred; persistence abstracted

- **Date:** 2026-08-18 (Phase 1: Discovery)
- **Decision:** Defer the database technology choice.  Persistence goes behind repository/store interfaces so SQLite, PostgreSQL, or SQL Server can be selected later without reworking the core.
- **Alternatives considered:** SQLite (recommended by agent), PostgreSQL container, SQL Server on Linux.
- **Rationale:** Hannah chose to defer; the abstraction keeps all options open.
- **Consequences:** Early phases use in-memory or file-backed stores for development until the decision is made.  Tracked in TODO.md.
- **Approval:** Deferral explicitly chosen by Hannah.

## D-0005: First inference runtime: llama.cpp (llama-server); no hardware or model assumptions in the core

- **Date:** 2026-08-18 (Phase 1: Discovery)
- **Decision:** The first inference adapter targets llama.cpp's `llama-server`.  Local development must not require production inference hardware: the dev workstation (Intel Core Ultra 7 165U, 32 GB RAM, CPU-only) runs a small quantized Qwen-class model, while production uses a substantially larger model on a dedicated V100 inference server.  The architecture must make no assumptions about model size, context window, inference latency, or accelerator availability.  Installing and configuring a local inference stack for test purposes is part of the project scope.
- **Alternatives considered:** Ollama (background service, easier model management), vLLM (recent versions dropped Volta/V100 support), LM Studio.
- **Rationale:** llama.cpp offers portable binaries, CPU-only GGUF support for dev, an OpenAI-compatible endpoint, and still supports Volta CUDA for production.
- **Consequences:** Model, quantization, context length, and endpoint are configuration values, never code assumptions.  The specific dev model/quantization is still to be selected (see TODO).
- **Approval:** Explicitly approved by Hannah.

## D-0006: Secrets: `ISecretProvider` abstraction; mounted secret files in production, .NET user-secrets in development

- **Date:** 2026-08-18 (Phase 1: Discovery)
- **Decision:** Secret retrieval goes behind a Viegard-owned `ISecretProvider` interface.  Production reads secrets from files mounted into the container at runtime.  Secrets are never stored in the image, source repository, Docker Compose YAML, or ordinary environment variables.  Development uses the .NET user-secrets mechanism.  Alternative implementations (Vault, SOPS/age, OS secret stores, cloud secret managers) must be addable without changing application code.  Each integration uses separate credentials with the minimum permissions required.  Secrets are never written to logs, diagnostics, exception messages, telemetry, documentation, or audit records.
- **Alternatives considered:** Environment variables; HashiCorp Vault; SOPS/age-encrypted config.  Docker Swarm secrets are out of scope because Hannah does not run Swarm.
- **Rationale:** File mounts avoid environment-variable leakage without requiring new infrastructure; the abstraction keeps stronger mechanisms open.
- **Approval:** Explicitly approved by Hannah.

## D-0007: Repository hosting: public Forgejo primary with public GitHub push mirror

- **Date:** 2026-08-18 (Phase 1: Discovery)
- **Decision:** The repository `viegard-sentinel` is hosted on Hannah's Forgejo instance (code.hannahvernon.com) as the primary, with a push mirror to GitHub (github.com/HannahVernon/viegard-sentinel).  Both are public; infrastructure details in commits, docs, and issues must stay sanitized.  The mirror pushes with a dedicated fine-grained GitHub PAT scoped to the mirror repository only, configured by Hannah in the Forgejo UI.
- **Alternatives considered:** Private on both; private Forgejo with public mirror; GitHub only.
- **Rationale:** Hannah's choice; keeps her Forgejo as the source of truth with GitHub for visibility.
- **Consequences:** Public-repo sanitization rules apply to all commits, PRs, issues, and documentation.  MIT license and community health files are required.
- **Approval:** Explicitly approved by Hannah.

## D-0008: License: MIT

- **Date:** 2026-08-18 (Phase 1: Discovery)
- **Decision:** The project is licensed under the MIT License.
- **Alternatives considered:** Apache-2.0, AGPL-3.0, no license.
- **Rationale:** Hannah's choice, consistent with her other public repositories.
- **Approval:** Explicitly approved by Hannah.

## D-0009: Commit author email for this repository

- **Date:** 2026-08-18 (Phase 1: Discovery)
- **Decision:** Commits in this repository use `hannah@mvct.com` (repo-local git config), not the work email in the global git config.
- **Approval:** Explicitly approved by Hannah.

## D-0010: Pipeline host and admin API are separate services

- **Date:** 2026-08-18 (Phase 2: Architecture)
- **Decision:** Viegard ships as two deployables: a pipeline host (workers: ingestion, correlation, classification, policy, actions) and a separate ASP.NET Core admin API host.
- **Alternatives considered:** Single host process combining workers and admin API (simpler operations, one container).
- **Rationale:** Stronger isolation between the security pipeline and the externally reachable admin surface.  A compromise or fault in the admin API does not run in the same process as credential-holding pipeline components.
- **Consequences:** The two services need a defined communication mechanism (see ARCHITECTURE.md proposal); deployment involves two containers.
- **Approval:** Explicitly approved by Hannah.

## D-0011: Process topology is extensible per service (role-configurable pipeline hosts)

- **Date:** 2026-08-18 (Phase 2: Architecture)
- **Decision:** The pipeline host is a role-configurable binary deployable N times, each instance running a configured subset of modules.  Data sources are multi-instance by configuration: Viegard must support many mail accounts across many providers (including self-hosted servers such as MDaemon), each as an isolated worker with its own credential, offsets, and health.  The correlator and the policy/action engine are singleton roles enforced by configuration validation.  Cross-process module communication uses a durable implementation of the same broker-semantics `IWorkQueue` port that in-process channels implement.
- **Alternatives considered:** Fixed single pipeline process (insufficient per Hannah's requirement); per-domain fixed split (mail host vs. security host); per-integration microprocesses (highest isolation, highest operational cost).
- **Rationale:** Hannah requires the number of processes to be extensible on a per-service basis; she monitors roughly ten mail accounts across four or five providers.
- **Consequences:** A database-backed durable queue is the first cross-process transport; singleton-role validation is required; deployment topology becomes a configuration concern, not a code concern.
- **Approval:** Requirement stated by Hannah; design shape (roles, singletons, transport) proposed by the agent within the pending ARCHITECTURE.md proposal.

## D-0012: Admin GUI must show per-queue traffic-light health status

- **Date:** 2026-08-18 (Phase 2: Architecture)
- **Decision:** The admin API/GUI includes a queue health monitor showing a traffic-light (green/amber/red) status per queue so that queues not being processed in a timely fashion are immediately visible.  Status derives from persisted telemetry (depth, oldest-unacknowledged-message age, consumer heartbeats, poison counts); stale telemetry is itself red.  Thresholds are configurable.
- **Alternatives considered:** Metrics-only exposure (e.g., dashboards in an external monitoring stack) without a first-class admin view; admin API querying pipeline processes directly (rejected: the pipeline exposes no inbound API surface).
- **Rationale:** Hannah requires immediate visibility of queue processing health as part of inter-service communication monitoring.
- **Consequences:** Pipeline hosts publish per-queue telemetry and heartbeats to shared persistence; the queue port must expose depth/oldest-age/ack statistics; amber/red thresholds become configuration values (defaults need Hannah's input).
- **Approval:** Requirement stated by Hannah; derivation design proposed by the agent within the pending ARCHITECTURE.md proposal.

## D-0013: MDaemon log ingestion added to initial data-source scope

- **Date:** 2026-08-18 (Phase 2: Architecture)
- **Decision:** A `Viegard.Sources.MDaemonLogs` adapter joins the solution layout: ingestion of MDaemon mail-server logs (SMTP/IMAP/POP session and screening logs) as evidence of email credential attacks.  Implementation order assumed as third data source (after IMAP and nginx/SWAG) pending Hannah's confirmation.
- **Alternatives considered:** Deferring all non-IMAP/nginx sources to a later phase (the prior default).
- **Rationale:** Hannah hosts her own MDaemon server; its logs are a rich source of detail about who is attempting to compromise email accounts.
- **Consequences:** Log selection, formats, paths, and the transport mechanism from the MDaemon server are new open questions (see TODO).
- **Approval:** Explicitly requested by Hannah.

## D-0014: Mobile-compatible admin GUI, Web Push, and operator status emails

- **Date:** 2026-08-18 (Phase 2: Architecture)
- **Decision:** The admin GUI is a mobile-compatible website so Hannah can monitor status and approve/deny actions from her phone.  The platform supports push notifications via the mobile website (Web Push) and operator status emails, both as `INotificationProvider` implementations (`Viegard.Notifications.WebPush`, `Viegard.Notifications.Email`).
- **Alternatives considered:** Desktop-only admin UI; native mobile app (heavier build/maintenance); third-party notification services such as ntfy/Pushover (additional dependency and data path).
- **Rationale:** Hannah's stated requirements for remote monitoring and approval.
- **Consequences:** Frontend technology, Web Push privacy sign-off (third-party push relays with E2E-encrypted payloads), SMTP notification details, and phone-to-GUI network access are new open questions (see TODO).  Admin authentication becomes still more consequential since approvals can originate from a phone.
- **Approval:** Requirements explicitly stated by Hannah; implementation shape pending her answers to the open questions.

## D-0015: Web Push technology decision deferred pending privacy review

- **Date:** 2026-08-18 (Phase 2: Architecture)
- **Decision:** The push-notification technology for the mobile admin GUI (part of D-0014) is deferred.  Browser Web Push routes deliveries through third-party relays (Google FCM, Apple, Mozilla); payloads are end-to-end encrypted (RFC 8291) but delivery timing/frequency metadata transits those clouds.  Hannah wants further consideration of the privacy implications before committing.  The mobile admin GUI and operator email notifications proceed unaffected; the notification port remains pluggable so a push mechanism (Web Push or an alternative such as a self-hosted ntfy/UnifiedPush server) can be added once decided.
- **What changed:** D-0014 originally included Web Push as the push mechanism; that portion is now an open, deferred decision.
- **Approval:** Deferral explicitly chosen by Hannah.

## D-0016: Admin GUI frontend: Blazor Web App (static SSR + Interactive Server islands) with automated staleness refresh

- **Date:** 2026-08-18 (Phase 2: Architecture)
- **Decision:** The admin GUI uses the .NET 10 Blazor Web App model: static server-side rendering by default, with Interactive Server components only where interactivity earns its keep (live queue traffic lights, approve/deny).  Approve/deny must also work as plain form posts.  The GUI must present an obvious UX hint whenever displayed data may be stale (dropped circuit, old telemetry, backgrounded tab) and automatically refresh, e.g., "Data is out of date, refreshing...".
- **Alternatives considered:** Blazor WASM PWA (multi-MB mobile first load, offline capability that buys nothing for server-data dashboards, browser-side token handling); Razor Pages + htmx (third-party JS dependency, hand-rolled dynamics); JS SPA (npm supply-chain exposure, framework churn, second toolchain).
- **Rationale:** Kilobyte-scale first paint for phone-first "glance and act" use; zero third-party frontend dependencies; server-side cookie auth; per-component WASM render modes remain available later, so this is not a one-way door.
- **Consequences:** Interactive islands depend on a SignalR circuit; the staleness-refresh UX requirement mitigates circuit drops on mobile.  Monitoring pages may use polling instead of circuits where simpler.
- **Approval:** Explicitly approved by Hannah, including the staleness-hint/auto-refresh requirement.

## D-0017: Phase 2 architecture approved

- **Date:** 2026-08-18 (Phase 2: Architecture)
- **Decision:** Hannah approved the ARCHITECTURE.md proposal as amended through D-0016, including its five assumptions: (1) two-container-plus deployment; (2) no inbound listener on pipeline hosts, admin-to-pipeline communication via shared persistence and a durable command queue; (3) broker-semantics queue port with in-process channels first; (4) MailKit (supply-chain review completed 2026-08-18: version 4.17.0, MIT, low risk); (5) role-configurable process topology with singleton correlator and policy/action engine.
- **Consequences:** Phase 3 (Skeleton) may begin: solution structure, configuration, logging, secrets abstraction, event model, plugin interfaces, health model, audit model, and test infrastructure.
- **Approval:** Explicitly approved by Hannah ("lets gooo").

## D-0018: Prompt-injection and AI-output validation implementation approach

- **Date:** 2026-08-18 (Phase 3: Skeleton)
- **Decision:** Two security-relevant implementation details, chosen by the agent within the approved architecture: (1) `PromptAssembler` renders SYSTEM / APPLICATION / UNTRUSTED-OBSERVED-DATA sections where untrusted values are emitted only inside data blocks delimited by a per-assembly cryptographically random boundary token (observed content cannot forge a closing delimiter), untrusted values can never fill template placeholders (hard error), and oversized values are truncated with an explicit marker.  (2) `ClassificationOutputValidator` is a strict, fail-closed, hand-rolled validator on System.Text.Json: required fields and ranges enforced, unknown top-level properties rejected, oversized output rejected, failures never carry partial data.  No third-party JSON-schema library was added.
- **Alternatives considered:** Third-party JSON Schema packages (JsonSchema.Net, NJsonSchema): rejected for now to keep the dependency surface minimal for a security-critical path; revisit if schema count grows.  Static delimiter strings: rejected because observed data could embed them.
- **Approval:** Implementation detail chosen by the agent; does not change any Hannah-approved behavior.

## D-0019: IMAP authentication: app passwords now, per-account mechanism seam for OAuth2 later

- **Date:** 2026-08-19 (Phase 4: Data sources)
- **Decision:** Each configured IMAP account declares its authentication mechanism.  `app-password` (SASL PLAIN/LOGIN over implicit TLS, credential via `ISecretProvider`) is implemented first and covers Yahoo, personal Gmail (2-Step Verification required), and self-hosted servers such as MDaemon.  The adapter exposes an authenticator seam so `oauth2` (XOAUTH2, e.g., MailKit `SaslMechanismOAuth2`) can be added per account later without changing the adapter core.
- **Alternatives considered:** OAuth2-only (Yahoo's developer-app approval process makes personal IMAP use impractical; Gmail personal does not require it); app-password-only with no seam (would force rework if a Google Workspace account joins, since Workspace dropped password-based IMAP access in 2024).
- **Rationale:** Hannah's requirement: use app passwords where they work (verified: Gmail personal accounts support them with 2SV), but build in the ability to support OAuth2 in the future.
- **Consequences:** Account configuration includes an auth-mechanism field; OAuth2 token acquisition/refresh is deferred until a concrete account needs it.
- **Approval:** Explicitly approved by Hannah.

## D-0020: New-mail detection: per-account IDLE with polling fallback plus safety poll

- **Date:** 2026-08-19 (Phase 4: Data sources)
- **Decision:** Each account is configurable: IMAP IDLE (push) where the server behaves, automatic fallback to polling after repeated IDLE failures, and a low-frequency safety poll while idling to catch anything IDLE misses.  IDLE sessions re-issue periodically per RFC 2177 guidance.
- **Alternatives considered:** IDLE-only (fragile against servers that drop idle connections); polling-only (adds latency and periodic load).
- **Rationale:** Roughly ten accounts across four or five heterogeneous servers; some will inevitably misbehave under IDLE.
- **Approval:** Explicitly approved by Hannah.

## D-0021: Default monitored folder set: INBOX, configurable per account

- **Date:** 2026-08-19 (Phase 4: Data sources)
- **Decision:** Monitor INBOX by default; each account's folder list is configurable.  Provider spam-folder names (e.g., Yahoo "Bulk Mail", Gmail "[Gmail]/Spam") become relevant for move actions in Phase 7, not ingestion defaults.
- **Alternatives considered:** INBOX + provider spam folder by default; fully explicit per-account lists with no default.
- **Approval:** Explicitly approved by Hannah.

## D-0022: Attachments: metadata only in Phase 4; content extraction deferred

- **Date:** 2026-08-19 (Phase 4: Data sources)
- **Decision:** Ingestion extracts attachment metadata only (filename, MIME type, size, disposition) and never downloads or opens attachment bodies.  Content extraction remains a separately controlled future subsystem.  Ingestion is strictly read-only: message retrieval uses IMAP PEEK so it never alters read/unread state.
- **Rationale:** Attachment parsing is a real attack surface for a security tool; defer until a concrete need exists.
- **Approval:** Explicitly approved by Hannah.

## D-0023: General syslog UDP ingestion source; assumption 2 revised

- **Date:** 2026-08-19 (Phase 4: Data sources)
- **Decision:** Viegard gains a general syslog ingestion source (`Viegard.Sources.Syslog`, UDP, RFC 3164/5424 envelopes) serving SWAG/nginx first (nginx logs natively to syslog over UDP; SWAG keeps file logging enabled as the durable record) and future senders such as MikroTik RouterOS remote logging.  This revises ARCHITECTURE.md assumption 2 (pipeline host previously had no inbound listener): the listener is permitted with mandatory guardrails: source-IP allowlist (fail-closed: no allowlist, no listener), per-source rate caps, datagram size cap, everything in the datagram (including claimed hostname/tag) treated as untrusted, origin identity keyed to peer IP.  Deployment is sequenced: the listener runs in the single pipeline instance now; after the database decision (D-0004) enables cross-process queues, it can be split into a credential-free listener-only instance by configuration alone (D-0011).
- **Alternatives considered:** SFTP pull from RouterOS (no inbound listener, but polling latency and no generalization to other senders); shipper container beside SWAG (robust TCP/TLS but new software on the router); SMB/NFS mount tailing (fragile).
- **Consequences:** UDP loss is possible (file logs remain source of truth; SFTP backfill remains a future option); LAN plaintext accepted by Hannah for the home network; nginx log format for the syslog target is Viegard-recommended configuration in SWAG.
- **Approval:** Explicitly approved by Hannah, including the sequenced deployment.












