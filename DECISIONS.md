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





