# Windows satellite installer

The Windows satellite installer publishes `Viegard.PipelineHost` on a Windows host and configures it for the `sources` role only.  A `-Client` profile selects which source prompts appear and which source configuration is written.  `MDaemon` is the only implemented profile today; it tails local MDaemon logs with `Viegard.Sources.MDaemonLogs` and writes observations, normalized events, offsets, queue messages, telemetry, and audit records to the shared PostgreSQL database on the Viegard VM.

## Assumptions

- The main Viegard stack on the VM owns database migrations.  The satellite installer writes `AutoMigrate=false` so a least-privilege satellite role does not need DDL permissions.
- The examples use placeholder addresses and role names.  Replace `192.0.2.10`, `192.0.2.21`, `192.0.2.22`, and the sample passwords with deployment-specific values before running anything.
- Run one satellite per log-producing host.  Each host needs a distinct `InstanceId`, for example `mdaemon-mail01` and `mdaemon-mail02`; two hosts must never share an instance ID.
- Service names are client-specific.  The MDaemon profile installs `ViegardSatelliteMDaemon`, so a future Windows satellite client can run on the same host without colliding with the MDaemon service.

## Prerequisites on each MDaemon host

- Windows Server 2019 or later with an elevated Windows PowerShell 5.1 session.
- Git for Windows in `PATH`.
- .NET 10 SDK in `PATH`.  Microsoft lists .NET 10 as supported on Windows Server 2019 x64 and Windows Server Core 2019 x64.  The installer checks `dotnet --list-sdks` and prints the official download page if no 10.x SDK is present: <https://dotnet.microsoft.com/download/dotnet/10.0>.
- MDaemon installed locally.  The installer defaults to `C:\MDaemon` and derives the log directory as `C:\MDaemon\Logs`.
- TCP reachability from the MDaemon host to the Viegard PostgreSQL listener on the VM, over TLS (see "Require TLS for satellite connections").
- A PostgreSQL role created for this satellite by the server-side steps below.

The script targets the stock Windows PowerShell 5.1 environment that ships with Windows Server 2019.  It avoids PowerShell 7-only syntax and uses `System.Net.Sockets.TcpClient` for the PostgreSQL reachability test instead of newer networking module features.

## Server-side preparation on the Viegard VM

### Publish PostgreSQL to the satellite hosts

The Docker Compose example keeps PostgreSQL private by default.  To accept satellite writes, publish `5432/tcp` on the VM address that the MDaemon hosts can reach:

```yaml
services:
  viegard-db:
    ports:
      - "192.0.2.10:5432:5432"
```

Publishing the port is only safe together with the two controls that follow: the `DOCKER-USER` firewall rule and PostgreSQL server TLS with `hostssl`-only `pg_hba.conf` lines.  Do not publish `5432/tcp` until both are in place.

Apply the compose change from the deploy directory:

```bash
docker compose up -d viegard-db
```

### Create a per-satellite database role

Use the admin UI as the primary path:

1. Sign in to the Viegard admin UI.
2. Complete step-up verification.
3. Open **Configuration** -> **Satellites**.
4. Enter the satellite name, for example `mdaemon01`, and choose **Add satellite**.  The server creates the PostgreSQL role as `viegard_sat_<name>`.
5. Copy the one-time password and the connection facts shown after the redirect.  The password cannot be retrieved again; it can only be rotated.
6. Paste the host, port, database, username, password, and schema into the installer prompts or unattended parameters.

The name is limited to lowercase letters, digits, and underscores, up to 32 characters.  Use one role per MDaemon host so credentials and audit trails stay separable.  Repeat the admin UI flow with a different satellite name for the second MDaemon host.

The application database role must have `CREATEROLE` to create, rotate, and revoke satellite roles from the UI.  If the UI reports that the application role lacks `CREATEROLE`, either grant that capability to the application database role or use the SQL fallback appendix below.

### What a satellite role may access (least privilege)

The satellite runs only the `sources` host role, so its role gets explicit per-table grants and nothing else (D-0063).  It has no access to admin users, sessions, app passwords, TOTP secrets, router credentials, policy or posture settings, or any other table that is not listed below.  There are no default privileges: a table added by a later migration is not granted to satellite roles until the grant list in `SatelliteGrantPolicy` names it, and a test fails if a model table is in neither the granted list nor the denied list.

The matrix below was derived by reading the code that a `sources`-only host runs: `PipelineStartupService` (instance registration, every 30 seconds), `IngestionWorker` and the MDaemon log source, `IngestionFilterRefreshWorker`, `QueueTelemetryPublisher`, and `HostUpgradeAgentWorker`, through the `Viegard.Persistence.Postgres` stores.  The Maintenance role (seeding and retention) does not run on satellites.  `AutoMigrate=false`, so no migration, DDL, or schema-version read happens.  `LISTEN` and `pg_notify` need no table privilege.

Table | Select | Insert | Update (columns) | Delete | Why the satellite needs it
------|--------|--------|------------------|--------|---------------------------
`sources` | yes | yes | `source_type` | no | `ReferenceResolver` finds or inserts the source row (`RETURNING id`) and fills `source_type` once when it is null.
`raw_observations` | `payload_reference` only | yes | none | no | Insert with `ON CONFLICT (payload_reference) DO NOTHING`.  The column-level SELECT supports the conflict arbiter.
`events` | no | yes | none | no | Normalized event rows.
`audit_records` | no | yes | none | no | Ingestion audit rows.
`queue_messages` | yes | yes | none | no | Durable enqueue (the satellite never leases, updates, or deletes); SELECT supports queue depth counts for telemetry.
`queue_counters` | yes | yes | `enqueued` | no | Enqueue upserts `enqueued = enqueued + 1`.
`queue_telemetry` | yes | yes | all columns | no | `QueueTelemetryPublisher` writes per-instance queue samples (find, then insert or update).
`source_offsets` | yes | yes | `value` | no | Log-tail position per file.
`ingestion_filters` | yes | no | none | no | Read-only filter configuration (`IngestionFilterRefreshWorker`).  Seeding is a Maintenance-role task.
`instance_registry` | `instance_id` only | yes | `commit_sha`, `host_name`, `reported_at`, `roles`, `started_at`, `upgrade_target`, `version` | no | Heartbeat upsert with `ON CONFLICT (instance_id) DO UPDATE`.  The column-level SELECT supports the conflict arbiter.
`host_upgrade_commands` | yes | no | `status`, `started_at`, `finished_at`, `detail` | no | `HostUpgradeAgentWorker` claims and completes upgrade commands for its own target.  INSERT stays with the VM admin role.

Sequences: `USAGE` on the identity sequences behind `sources.id` and `queue_messages.id`, looked up with `pg_get_serial_sequence` so the grant does not depend on a sequence name.

Foreign key checks run with the privileges of the table owner, so the referenced tables need no extra grant.

Confidence: high for every row, because each comes from reading the store code that the `sources` role executes.  Two details are inferred from PostgreSQL behaviour and need a run against a real PostgreSQL to confirm: the column-level SELECT on `raw_observations.payload_reference` and `instance_registry.instance_id` that supports `ON CONFLICT`, and the exact `GRANT` statements succeeding.

Residual risks that remain after this change:

- **Queue name.** INSERT on `queue_messages` does not restrict `queue_name`, so a compromised satellite can enqueue into queues other than the one it uses.  Restricting this needs a row-level security policy and a migration; it is a candidate for the ingest-API redesign.
- **Upgrade commands.** UPDATE on four columns of `host_upgrade_commands` cannot be limited to the satellite's own `target`.  A compromised satellite can change the status, timestamps, and detail of other targets' commands (for example the `vm` target), but it cannot create commands, delete them, or change `target`.  Row-level security keyed on the target was evaluated and not adopted in this change: the target is a free-form per-satellite setting (it defaults to the lowercase computer name and is not derived from the role name), so enforcing it needs a trusted role-to-target mapping table, a migration, and code to maintain the mapping.
- **Instance and telemetry rows.** UPDATE on `instance_registry` and `queue_telemetry` is not restricted to the satellite's own rows, so a compromised satellite can overwrite another instance's version or queue samples.  These rows are informational.
- **Reads.** The satellite can read `queue_messages` (event payloads) and the other tables it can SELECT.  It cannot read the admin, credential, router, or policy tables.

### Restrict published PostgreSQL traffic (required)

Restricting the published database port is a required step before satellites connect, not an option.  Docker published ports bypass the host `INPUT` chain, so restrictions belong in `DOCKER-USER`.  Put the allow rules before the drop rule, and include every legitimate client (satellite hosts, plus any administration workstations that connect with tools such as pgAdmin):

```bash
sudo iptables -I DOCKER-USER 1 -p tcp --dport 5432 -s 192.0.2.21 -j RETURN
sudo iptables -I DOCKER-USER 2 -p tcp --dport 5432 -s 192.0.2.22 -j RETURN
sudo iptables -I DOCKER-USER 3 -p tcp --dport 5432 -j DROP
sudo netfilter-persistent save
```

Use the actual client addresses in place of `192.0.2.21` and `192.0.2.22`.  The rule is the first of three controls (firewall, TLS, `pg_hba.conf`); do not publish `5432/tcp` without it.

### Require TLS for satellite connections

The satellite connection must be encrypted and the server certificate must be validated (D-0062).  The installer writes `Viegard:Database:SslMode` as `VerifyFull` by default, and the application code also defaults to `VerifyFull` for any database host that is not loopback or the compose service name.

1. Create a server certificate for the PostgreSQL host name or address that the satellites use.  For `VerifyFull`, that name must appear in the certificate (subject alternative name), including when satellites connect by IP address.  Use a certificate that chains to a root the satellite trusts, or pass the root with `-PostgresRootCertificate`.
2. Enable TLS on the PostgreSQL server (`postgresql.conf`, or `-c` flags on the container command): `ssl = on`, `ssl_cert_file`, and `ssl_key_file`.  The key file must be readable only by the PostgreSQL service user (mode 0600, or 0640 when root owns it and the group is the PostgreSQL group).  Restart PostgreSQL; `ssl` is not reloadable.
3. In `pg_hba.conf`, allow each satellite role only over TLS with SCRAM, and reject the same role without TLS.  Rules are matched in order, so put these before any broader rule:

```
hostssl    viegard  viegard_sat_mdaemon01  192.0.2.21/32  scram-sha-256
hostnossl  all      viegard_sat_mdaemon01  0.0.0.0/0      reject
hostnossl  all      viegard_sat_mdaemon01  ::/0           reject
```

   Do not add `host` (any-transport) lines for `viegard_sat_` roles.  Confirm that `password_encryption = scram-sha-256` (the PostgreSQL 14 and later default) so the generated role passwords are stored as SCRAM verifiers.  Reload with `SELECT pg_reload_conf();` or `pg_ctl reload`.
4. Pass the root certificate to the installer when the server certificate does not chain to a root that Windows already trusts:

```powershell
.\deploy\windows\viegard-satellite.ps1 install -Client MDaemon `
    -PostgresSslMode VerifyFull `
    -PostgresRootCertificate C:\Viegard\certs\postgres-root.crt
```

   The installer accepts `Require`, `VerifyCA`, or `VerifyFull` for `-PostgresSslMode`.  `Require` encrypts without validating the server, so it does not defend against an impersonated server; use it only as a temporary step.  For an existing satellite, edit `Viegard:Database:SslMode` and `Viegard:Database:RootCertificate` in the service's `appsettings.Production.json`; the upgrade configuration merge preserves existing values.

The Docker Compose example sets `Viegard__Database__SslMode: Disable` for the pipeline and admin containers because they reach the `viegard-db` container over the private compose network.  The code permits `Disable`, `Allow`, and `Prefer` only for loopback hosts and that compose service name; any other host must use `Require`, `VerifyCA`, or `VerifyFull`.

The `pg_hba.conf` syntax, the TLS settings, and the compose container flags above have not been run against a live server as part of this change; verify them in a test environment first.

### SQL fallback for satellite role creation

The admin UI is the recommended path.  Operators who prefer SQL, or deployments where the application role does not have `CREATEROLE`, can run the fallback manually as the database role that owns the Viegard tables (the application role, normally `viegard`), so that the `REVOKE` statements and the default-privilege cleanup apply to that role's objects.  Replace the role name, password, and schema if they differ from the defaults.

```sql
CREATE ROLE "viegard_sat_mdaemon01"
    LOGIN
    PASSWORD 'replaceWithLongRandomAlphanumericPassword'
    NOSUPERUSER
    NOCREATEDB
    NOCREATEROLE
    NOINHERIT
    CONNECTION LIMIT 16;
```

Then apply the grants with the re-apply snippet in the next section.

### Re-apply grants to existing satellite roles

Roles created before D-0063 hold `SELECT, INSERT, UPDATE, DELETE` on every table and `ALTER DEFAULT PRIVILEGES` entries that grant the same on future tables.  They keep those privileges until the grants are re-applied.  Use **Configuration** -> **Satellites** -> **Re-apply grants** for each role (step-up required; the action is written to the configuration audit log), or run the SQL below as the table owner.  The statements are idempotent: the first four remove every existing table and sequence privilege and the default-privilege entries for the role, and the rest grant the matrix.

```sql
REVOKE ALL ON ALL TABLES IN SCHEMA "viegard" FROM "viegard_sat_mdaemon01";
REVOKE ALL ON ALL SEQUENCES IN SCHEMA "viegard" FROM "viegard_sat_mdaemon01";
ALTER DEFAULT PRIVILEGES FOR ROLE CURRENT_USER IN SCHEMA "viegard" REVOKE ALL ON TABLES FROM "viegard_sat_mdaemon01";
ALTER DEFAULT PRIVILEGES FOR ROLE CURRENT_USER IN SCHEMA "viegard" REVOKE ALL ON SEQUENCES FROM "viegard_sat_mdaemon01";
GRANT USAGE ON SCHEMA "viegard" TO "viegard_sat_mdaemon01";
GRANT SELECT, INSERT ON TABLE "viegard"."sources" TO "viegard_sat_mdaemon01";
GRANT UPDATE ("source_type") ON TABLE "viegard"."sources" TO "viegard_sat_mdaemon01";
GRANT INSERT ON TABLE "viegard"."raw_observations" TO "viegard_sat_mdaemon01";
GRANT SELECT ("payload_reference") ON TABLE "viegard"."raw_observations" TO "viegard_sat_mdaemon01";
GRANT INSERT ON TABLE "viegard"."events" TO "viegard_sat_mdaemon01";
GRANT INSERT ON TABLE "viegard"."audit_records" TO "viegard_sat_mdaemon01";
GRANT SELECT, INSERT ON TABLE "viegard"."queue_messages" TO "viegard_sat_mdaemon01";
GRANT SELECT, INSERT ON TABLE "viegard"."queue_counters" TO "viegard_sat_mdaemon01";
GRANT UPDATE ("enqueued") ON TABLE "viegard"."queue_counters" TO "viegard_sat_mdaemon01";
GRANT SELECT, INSERT, UPDATE ON TABLE "viegard"."queue_telemetry" TO "viegard_sat_mdaemon01";
GRANT SELECT, INSERT ON TABLE "viegard"."source_offsets" TO "viegard_sat_mdaemon01";
GRANT UPDATE ("value") ON TABLE "viegard"."source_offsets" TO "viegard_sat_mdaemon01";
GRANT SELECT ON TABLE "viegard"."ingestion_filters" TO "viegard_sat_mdaemon01";
GRANT INSERT ON TABLE "viegard"."instance_registry" TO "viegard_sat_mdaemon01";
GRANT SELECT ("instance_id") ON TABLE "viegard"."instance_registry" TO "viegard_sat_mdaemon01";
GRANT UPDATE ("commit_sha", "host_name", "reported_at", "roles", "started_at", "upgrade_target", "version") ON TABLE "viegard"."instance_registry" TO "viegard_sat_mdaemon01";
GRANT SELECT ON TABLE "viegard"."host_upgrade_commands" TO "viegard_sat_mdaemon01";
GRANT UPDATE ("status", "started_at", "finished_at", "detail") ON TABLE "viegard"."host_upgrade_commands" TO "viegard_sat_mdaemon01";
GRANT USAGE ON SEQUENCE "viegard"."sources_id_seq" TO "viegard_sat_mdaemon01";
GRANT USAGE ON SEQUENCE "viegard"."queue_messages_id_seq" TO "viegard_sat_mdaemon01";
```

The two sequence names above are the PostgreSQL implicit names for identity columns; confirm them with `SELECT pg_get_serial_sequence('"viegard"."sources"', 'id');` (and the same for `queue_messages`) before running the snippet by hand.  The admin UI action looks the names up itself.

To check a role afterward, run `SELECT has_table_privilege('viegard_sat_mdaemon01', 'viegard.admin_users', 'SELECT');` (expect `false`) and `SELECT has_column_privilege('viegard_sat_mdaemon01', 'viegard.host_upgrade_commands', 'target', 'UPDATE');` (expect `false`).

Roles created before 2026-09-16 carry `CONNECTION LIMIT 8`; raise them so the satellite's connection pool plus its LISTEN connection fit with headroom:

```sql
ALTER ROLE "viegard_sat_mdaemon01" CONNECTION LIMIT 16;
```

The installer writes `Viegard:Database:MaxPoolSize` 8 into the satellite configuration so the Npgsql pool stays well inside the role limit.  The installer also registers the `Viegard.PipelineHost` Windows event log source during install and upgrade; without it, the service cannot write crash reports to the Application log.

## Install on the MDaemon host

Clone the repository anywhere on the MDaemon server (this bootstrap clone only supplies the installer script and the origin URL), then run the installer:

```powershell
git clone --branch dev https://code.hannahvernon.com/hannah-vernon/viegard-sentinel.git C:\Viegard
Set-Location C:\Viegard
.\deploy\windows\viegard-satellite.ps1 install -Client MDaemon
```

The installer creates a **managed clone** at `C:\Program Files\Viegard Satellite\repo` (override with `-RepoDir`; supply `-CloneUrl` when not running from a clone).  All git operations, publishes, the scheduled task, and the service-side upgrade agent use the managed clone, never the bootstrap clone.  Placing it under Program Files means every ancestor directory is writable only by administrators, so no other local user can plant a `.git` directory anywhere along the resolved path.  The installer also whitelists the managed clone in git's system-level `safe.directory` configuration so git commands succeed for the service account and for the `SYSTEM` scheduled task, which do not own the clone.  After install, the bootstrap clone can be deleted.

For an unattended or repeatable run, pass the prompted values as parameters:

```powershell
$dbPassword = Read-Host -Prompt "PostgreSQL password" -AsSecureString
.\deploy\windows\viegard-satellite.ps1 install -Yes `
    -Client MDaemon `
    -ServiceAccount VirtualAccount `
    -MDaemonRoot C:\MDaemon `
    -PostgresHost 192.0.2.10 `
    -PostgresPort 5432 `
    -PostgresDatabase viegard `
    -PostgresUsername viegard_sat_mdaemon01 `
    -PostgresPassword $dbPassword `
    -PostgresSchema viegard `
    -InstanceId mdaemon-mail01 `
    -HostUpgradeTarget mail01 `
    -MDaemonLogKinds SmtpIn,SmtpOut,Imap,Pop3,Screening,DynamicScreening
```

The default install directory is `C:\Program Files\Viegard Satellite\MDaemon`.  The client name is part of the default path so future Windows satellite clients can be installed on the same host without sharing app/config/secrets folders.  The app is published to the `app` subdirectory and the database password is stored in `secrets\viegard-db-password` with no trailing newline.

### Installer prompts

The installer asks for:

- Client profile.  Valid value today: `MDaemon`.
- MDaemon root folder.  Default: `C:\MDaemon`.  It must contain a `Logs` subfolder.
- MDaemon log directory.  Default: `<MDaemon root>\Logs`.
- Service account choice.  Default and recommendation: `VirtualAccount`.
- PostgreSQL host.
- PostgreSQL port.  Default: `5432`.
- PostgreSQL database.  Default: `viegard`.
- PostgreSQL username.
- PostgreSQL password as a `SecureString`.  Existing secret files are kept and not overwritten.
- PostgreSQL schema.  Default: `viegard`.
- Instance ID.  This is required and has no default because instance IDs must be unique across every host that runs a satellite.  The prompt suggests `mdaemon-<hostname>`.  The MDaemon source derives a safe per-instance key from this value for file offsets and raw-observation payload references, so two satellites can ingest identical MDaemon-generated file names without sharing offsets or duplicate-detecting each other's lines.
- Admin UI upgrade target.  Default: the lowercase Windows computer name.  It must use lowercase letters, digits, dash, and underscore, and it must not be `vm`.
- MDaemon log kinds.  Valid values are `SmtpIn`, `SmtpOut`, `Imap`, `Pop3`, `Screening`, and `DynamicScreening`.  The default is all supported kinds.

Before changing files or services, the installer checks that the log directory is readable and that the PostgreSQL host and port accept TCP connections.  If PostgreSQL is unreachable, the installer continues only after explicit confirmation.

### Service account choices

Choice | Account | Trade-off
-------|---------|----------
`VirtualAccount` | `NT SERVICE\ViegardSatelliteMDaemon` | Recommended default.  It has no password to manage and receives ACLs scoped to this service.
`LocalService` | `NT AUTHORITY\LOCAL SERVICE` | No password to manage, but the account is shared by other LocalService services, so file ACLs are broader than the virtual-account option.
`Custom` | A named local or domain account | Useful when local policy requires a managed service identity.  The installer prompts for the account name and password, passes them to `sc.exe`, and never prints the password.  Password rotation becomes an operator responsibility.

### What the installer creates

- A managed clone of the repository at `C:\Program Files\Viegard Satellite\repo`, shared by all client profiles on the host, whitelisted in git's system-level `safe.directory` configuration.
- A self-contained win-x64 publish of `src\Viegard.PipelineHost` under `C:\Program Files\Viegard Satellite\MDaemon\app`.
- `appsettings.Production.json` in the publish output.  It sets `Viegard:WindowsService:ServiceName` to `ViegardSatelliteMDaemon`, sets `Viegard:Host:Roles` to `["sources"]`, configures `Viegard:HostUpgradeAgent` for this satellite target (including `CloneRoot` pinned to the managed clone), configures PostgreSQL persistence, points the file secret provider at the local secrets directory, enables the MDaemon source, and sets `Viegard:Database:AutoMigrate` to `false`.
- `C:\Program Files\Viegard Satellite\MDaemon\secrets\viegard-db-password`, never printed and never stored in JSON.
- Windows service `ViegardSatelliteMDaemon`, display name `Viegard Satellite Pipeline (MDaemon)`, delayed automatic start, and restart recovery at 1, 5, and 15 minutes.
- Registry environment value `DOTNET_ENVIRONMENT=Production` for the service.
- ACLs granting the selected service account read access to the MDaemon logs and to the local secret file.  The secrets directory is restricted to SYSTEM, Administrators, and that service account.

After the service starts, check the Viegard admin UI `/queues` page.  The Instances table should show the new instance ID, the short build commit in the Version column, the start age, and telemetry freshness.  The Queues table should continue to show one shared row per queue.

Routine MDaemon transcript chatter can be filtered centrally from **Configuration** -> **Ingestion**.  The default filter suppresses `SessionLine` and `Other` before they become normalized events, which reduces queue and correlation load on every upgraded sources-role instance.  Raw observations still record every tailed log payload on the satellite for forensic review.

## Upgrade

Run upgrade from any copy of the script in an elevated Windows PowerShell session:

```powershell
.\deploy\windows\viegard-satellite.ps1 upgrade -Client MDaemon
```

The upgrade command operates on the managed clone at `C:\Program Files\Viegard Satellite\repo`, creating it first when it does not exist (hosts installed before the managed clone existed migrate automatically on their next upgrade; the URL and branch come from the clone containing the running script, or from `-CloneUrl`).  It refuses to run with local git changes, fetches origin, reports incoming commits, and runs `git pull --ff-only`.  It then compares the current clone HEAD with `app\.deployed-commit` under the resolved install directory.  It stops the service, republishes the app, preserves `appsettings.Production.json` and `secrets\viegard-db-password`, restarts the service, and verifies that the service reaches `Running` with no new `Viegard.PipelineHost` Error-level Application events in the startup window when the marker is missing, differs from clone HEAD, or `-Force` is supplied.  Warning-level entries from the startup window are printed for context but do not fail the upgrade.  It exits without republishing only when the marker exists and matches clone HEAD.  Every successful publish writes the clone's full HEAD SHA to `app\.deployed-commit`.

Upgrade also performs a configuration merge after the preserved JSON is restored.  Missing `Viegard:Database:MaxPoolSize` is added with value `8` (`SslMode` is not merged; a missing value resolves to `VerifyFull` for a remote host), and missing `Viegard:HostUpgradeAgent` keys are added from the current client profile, scheduled-task name, prompted upgrade target, and per-target state directory under `C:\ProgramData\Viegard\<target>`.  The installer-owned path values `SatelliteScriptPath` and `CloneRoot` are corrected to the managed clone when they point elsewhere; all other existing values are not overwritten.  Upgrade also repoints an existing auto-upgrade scheduled task at the managed clone's script, preserving its schedule.  If the binaries are already current but the merge changes settings, the script restarts the service so they take effect.  With `-Yes`, the upgrade target prompt accepts the lowercase machine-name default.

## Upgrade ordering

Satellites never run migrations (`AutoMigrate=false`) and the satellite code does not check the schema version: it does not read `__ef_migrations_history`, the `instance_registry` version column, or any other version information from the database.  The only database use tied to upgrades is `HostUpgradeAgentWorker`, which claims and completes rows in `host_upgrade_commands` for its own target; its local version information (`.deployed-commit` against the managed clone HEAD) is file-based.  Because nothing compares build and schema, the order of operations decides whether a mismatch causes failures.  Use this order for every release that changes the schema, the grant matrix, or TLS requirements:

1. **Upgrade the VM first.**  The VM admin and pipeline containers have `AutoMigrate` enabled and apply the migrations at startup.  The satellites keep working against the new schema only if the migration is compatible with their build (see the mismatch behaviour below).
2. **Re-apply the grants for every existing satellite role** (**Configuration** -> **Satellites** -> **Re-apply grants**, or the SQL in "Re-apply grants to existing satellite roles") before or together with upgrading the satellites, and always before relying on the new build.  Roles created before D-0063 keep their broad grants until this step.  Roles created by the new build already have the explicit grants.  A satellite build that needs a table that is not in the grant list fails with `permission denied` until the list is updated and re-applied, because there are no default privileges.
3. **Confirm the role before upgrading the satellite.**  The satellite's remote-upgrade path uses `host_upgrade_commands`, so a wrong grant there stops UI-driven upgrades.  After re-granting, check that the satellite's row in the `/queues` Instances table keeps updating its heartbeat, and that a test upgrade request from **Configuration** -> **Upgrades** moves from `Pending` to `Succeeded`.
4. **Upgrade the satellites** (UI-driven, scheduled, or `upgrade` from the script).

When a release also tightens TLS, enable PostgreSQL server TLS (`ssl = on`) first while `host` lines are still permitted, then upgrade the satellites, and only then switch the satellite roles to `hostssl`-only in `pg_hba.conf`.  Earlier satellite builds connect with `Prefer`, which uses TLS when the server offers it.  The new build defaults to `VerifyFull` for a remote host, so a satellite upgraded before the server offers a certificate that matches the configured host name cannot connect, and because the upgrade agent runs inside that same service, the UI cannot repair it remotely; fix it on the host by editing `Viegard:Database` in `appsettings.Production.json`.

What the code does when the build and the schema differ (observed from the code, not exercised against a live database):

- **Satellite newer than the schema.**  The satellite issues its normal queries and PostgreSQL rejects any that reference a missing table or column.  There is no startup check, so the service starts, and the failure appears as repeated errors in the Application event log from the affected worker.  How ingestion behaves while that happens (retry, stall, or loss of the failed batch) was not traced and should be confirmed in a test environment before relying on it.  Upgrade the VM first so this state does not occur.
- **Satellite older than the schema.**  There is no version gate.  An older satellite keeps working if the new migrations only add nullable columns or new tables, and fails if a migration drops or renames a column or table it uses, or adds a required column to a table it inserts into.  Nothing in the code enforces or documents an N-1 compatibility window, so treat N-1 tolerance as unsupported unless a release note states otherwise.
- **Grant matrix and build.**  The matrix is derived from the current code.  An older build that touches a table outside the matrix gets `permission denied` after the re-grant; this was not found in the current code, but older builds were not traced.

## Scheduled auto-upgrade

The satellite can register a Windows Scheduled Task that runs the existing git-based upgrade command as `SYSTEM`.  The default schedule is weekly on Sunday at 03:30 local time:

```powershell
.\deploy\windows\viegard-satellite.ps1 register-autoupgrade -Client MDaemon
```

Choose a different local time with `-Time HH:mm`:

```powershell
.\deploy\windows\viegard-satellite.ps1 register-autoupgrade -Client MDaemon -Time 04:15
```

Use `-Daily` for a daily schedule:

```powershell
.\deploy\windows\viegard-satellite.ps1 register-autoupgrade -Client MDaemon -Daily -Time 03:30
```

The task action runs from the managed clone directory and invokes:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\Program Files\Viegard Satellite\repo\deploy\windows\viegard-satellite.ps1" upgrade -Client MDaemon -Yes
```

The task name for the MDaemon profile is `ViegardSatelliteMDaemonAutoUpgrade`.  The installer writes the same name into `Viegard:HostUpgradeAgent:ScheduledTaskName`, so the service-side upgrade agent and the registered scheduled task stay aligned.  Registration also grants the satellite service account permission to read and start the task, which UI-driven upgrades require; upgrades re-apply the grant so existing installs converge.

The upgrade command exits quickly when the deployed marker matches clone HEAD, so the scheduled task is safe to leave in place.  Remove it with:

```powershell
.\deploy\windows\viegard-satellite.ps1 unregister-autoupgrade -Client MDaemon
```

## UI-driven satellite upgrades

The Admin UI can request a satellite upgrade from **Configuration** -> **Upgrades** by selecting a known target or typing a new one.  Known targets come from prior upgrade command history plus `vm`; typing a new target is valid as long as it follows the same lowercase target character set.  A request only queues a fixed-verb row in `host_upgrade_commands`; it does not give the Admin UI Windows execution rights.

Each Windows satellite claims only the target written in its local `Viegard:HostUpgradeAgent:Target` setting.  The MDaemon installer writes this section during install, and upgrades merge it into older preserved configurations without changing existing values.  The default target is the lowercase Windows computer name, so each host naturally has a distinct target unless the operator chooses another valid value.

When the service claims a command, it writes `viegard-host-upgrade-state.json` in `Viegard:HostUpgradeAgent:StateDirectory`, records the clone HEAD before the upgrade, and tries to run the scheduled task first.  Task Scheduler's default security descriptor lets only Administrators and `SYSTEM` start a task, so the installer grants the satellite service account read and execute permission on the auto-upgrade task during `register-autoupgrade` and on every `upgrade`; without that grant, the service-side launch fails with access denied.  Current installs default the state directory to `C:\ProgramData\Viegard\<target>` and grant the service account Modify permission during install and upgrade.  This supersedes the earlier app-directory location under `C:\Program Files\Viegard Satellite\<Client>\app`, which non-privileged service accounts could not write.  The scheduled task runs as `SYSTEM` with highest privileges and executes the same git-based `upgrade -Client MDaemon -Yes` command documented above.  If the task cannot be started, the worker falls back to a detached elevated PowerShell launch that writes `viegard-host-upgrade-transcript.log` in the same state directory.  The fallback trusts only that transcript: elevation can silently produce no process (a non-admin service account in session 0 has no elevation path), so if the transcript does not appear within 30 seconds the command is marked Failed with detail pointing at the scheduled-task permission repair, instead of reporting a launch that never happened.

Completion is reported by the service after it restarts.  On startup, the worker reads the state file, reads `app\.deployed-commit`, reads the clone HEAD from the configured `Viegard:HostUpgradeAgent:CloneRoot` (older configurations without `CloneRoot` require the `.git` directory exactly two levels above `SatelliteScriptPath`; in both cases the resolver fails closed rather than searching ancestor directories), and marks the command succeeded only when the marker and clone HEAD match.  The agent passes `safe.directory` inline on its git call, so the read works even before the installer's system-level whitelist exists.  If the upgrade command decides the satellite is already current and does not restart the service, the worker sees the lingering state file after `Viegard:HostUpgradeAgent:StuckStateGracePeriod` (default 10 minutes, valid range 1 to 60 minutes), runs the same marker-vs-HEAD check, and completes the command without launching anything else.  While a state file exists and is still inside the grace period, the worker skips polling so it cannot double-launch a requeued command.  The command detail contains the before-and-after short commits and a bounded transcript tail when one exists.

Status meanings in the Admin UI:

- `Pending`: the command has been queued but no agent for that target has claimed it yet.  This is expected if the target is mistyped or the satellite is offline.
- `Running`: an agent claimed the command and launched the upgrade path.  If the service never stops because the upgrade was already current, the same worker completes it after the stuck-state grace period.  If the upgrade stops the service but never brings it back, it remains Running until the agent starts again and reconciles the local state.
- `Succeeded`: the restarted service verified that `.deployed-commit` matches clone HEAD.
- `Failed`: the agent could not launch the upgrade, the restarted service found a marker mismatch, or startup reconciliation found a Running command for its target with no local state file.

## Status

Status does not change the service:

```powershell
.\deploy\windows\viegard-satellite.ps1 status -Client MDaemon
```

It reports the service state, uptime when running, install directory, clone commit, deployed commit marker, configured instance ID, PostgreSQL target, TCP reachability, and recent matching Application event-log entries.  A missing deployed marker is shown as `unknown - predates commit tracking`; a mismatch between clone HEAD and the deployed marker prints an explicit warning.

## Troubleshooting: service relocated to a wrong directory

Older script versions could register the service with an unquoted image path.  On a path containing spaces, a later `upgrade` then resolved the install root as `C:\`, published to `C:\app`, and re-pointed the service there, leaving `appsettings.Production.json` behind in the original install directory.  Symptoms: the service runs but produces no telemetry or events, `status` reports the install directory as `C:\` and warns that no readable `appsettings.Production.json` was found, and services.msc shows the executable under `C:\app`.

Recovery, in an elevated session from the cloned repository (after `git pull` to get the fixed script):

```powershell
Stop-Service -Name ViegardSatelliteMDaemon
.\deploy\windows\viegard-satellite.ps1 install -Client MDaemon -InstallDir "C:\Program Files\Viegard Satellite\MDaemon"
Remove-Item -LiteralPath "C:\app" -Recurse -Force
```

The explicit `-InstallDir` overrides the misregistered service path.  Install re-prompts for configuration values, keeps the existing database password secret file, republishes, re-registers the service with an enforced quoted image path, and restarts.  Current script versions enforce the quoted image path in the registry on every install and upgrade, and `upgrade` refuses to publish into a directory that does not already contain a satellite deployment.

## Uninstall

Run from an elevated Windows PowerShell session:

```powershell
Stop-Service -Name ViegardSatelliteMDaemon -ErrorAction SilentlyContinue
sc.exe delete ViegardSatelliteMDaemon
Remove-Item -LiteralPath "C:\Program Files\Viegard Satellite\MDaemon" -Recurse -Force
```

Then remove the corresponding database role after confirming no other host uses it.  The preferred path is **Configuration** -> **Satellites** -> **Revoke** in the admin UI.  Stop the satellite service first so there is no active database connection.  SQL fallback:

```sql
DROP OWNED BY "viegard_sat_mdaemon01";
DROP ROLE "viegard_sat_mdaemon01";
```

Remove or adjust the matching `DOCKER-USER` firewall allow rule if the host is being retired.
