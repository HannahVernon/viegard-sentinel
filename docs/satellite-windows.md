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
- TCP reachability from the MDaemon host to the Viegard PostgreSQL listener on the VM.
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

This is intentionally a broad v1 DML grant on the Viegard schema.  The satellite runs only `sources`, but that role still writes raw observations, normalized events, source offsets, source reference rows, durable queue rows, queue telemetry, and audit records, and it reads shared configuration and reference rows.  A narrower table-specific grant can replace this later after the satellite write surface is measured.

### Restrict published PostgreSQL traffic (recommended)

Restricting the published database port is recommended defense in depth, not a requirement for the satellite to function.  Docker published ports bypass the host `INPUT` chain, so restrictions belong in `DOCKER-USER`.  Put the allow rules before the drop rule, and include every legitimate client (satellite hosts, plus any administration workstations that connect with tools such as pgAdmin):

```bash
sudo iptables -I DOCKER-USER 1 -p tcp --dport 5432 -s 192.0.2.21 -j RETURN
sudo iptables -I DOCKER-USER 2 -p tcp --dport 5432 -s 192.0.2.22 -j RETURN
sudo iptables -I DOCKER-USER 3 -p tcp --dport 5432 -j DROP
sudo netfilter-persistent save
```

Use the actual client addresses in place of `192.0.2.21` and `192.0.2.22`.  Skipping this leaves PostgreSQL's own authentication as the only gate on the published port; the LAN threat model is the operator's call.

### SQL fallback for satellite role creation

The admin UI is the recommended path.  Operators who prefer SQL, or deployments where the application role does not have `CREATEROLE`, can run the fallback manually as a database role that can create roles and grant privileges.  Replace the role name, password, database role in `FOR ROLE`, and schema if they differ from the defaults.

```sql
CREATE ROLE "viegard_sat_mdaemon01"
    LOGIN
    PASSWORD 'replaceWithLongRandomAlphanumericPassword'
    NOSUPERUSER
    NOCREATEDB
    NOCREATEROLE
    NOINHERIT
    CONNECTION LIMIT 8;

GRANT USAGE
    ON SCHEMA "viegard"
    TO "viegard_sat_mdaemon01";

GRANT SELECT, INSERT, UPDATE, DELETE
    ON ALL TABLES IN SCHEMA "viegard"
    TO "viegard_sat_mdaemon01";

GRANT USAGE, SELECT
    ON ALL SEQUENCES IN SCHEMA "viegard"
    TO "viegard_sat_mdaemon01";

ALTER DEFAULT PRIVILEGES FOR ROLE "viegard" IN SCHEMA "viegard"
    GRANT SELECT, INSERT, UPDATE, DELETE
    ON TABLES
    TO "viegard_sat_mdaemon01";

ALTER DEFAULT PRIVILEGES FOR ROLE "viegard" IN SCHEMA "viegard"
    GRANT USAGE, SELECT
    ON SEQUENCES
    TO "viegard_sat_mdaemon01";
```

## Install on the MDaemon host

Clone the repository on the MDaemon server, then run the installer.  No manual `dotnet publish` or hand-edited JSON is required.

```powershell
git clone --branch dev https://code.hannahvernon.com/hannah-vernon/viegard-sentinel.git C:\Viegard
Set-Location C:\Viegard
.\deploy\windows\viegard-satellite.ps1 install -Client MDaemon
```

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
- Instance ID.  This is required and has no default because instance IDs must be unique across every host that runs a satellite.  The prompt suggests `mdaemon-<hostname>`.
- MDaemon log kinds.  Valid values are `SmtpIn`, `SmtpOut`, `Imap`, `Pop3`, `Screening`, and `DynamicScreening`.  The default is all supported kinds.

Before changing files or services, the installer checks that the log directory is readable and that the PostgreSQL host and port accept TCP connections.  If PostgreSQL is unreachable, the installer continues only after explicit confirmation.

### Service account choices

Choice | Account | Trade-off
-------|---------|----------
`VirtualAccount` | `NT SERVICE\ViegardSatelliteMDaemon` | Recommended default.  It has no password to manage and receives ACLs scoped to this service.
`LocalService` | `NT AUTHORITY\LOCAL SERVICE` | No password to manage, but the account is shared by other LocalService services, so file ACLs are broader than the virtual-account option.
`Custom` | A named local or domain account | Useful when local policy requires a managed service identity.  The installer prompts for the account name and password, passes them to `sc.exe`, and never prints the password.  Password rotation becomes an operator responsibility.

### What the installer creates

- A self-contained win-x64 publish of `src\Viegard.PipelineHost` under `C:\Program Files\Viegard Satellite\MDaemon\app`.
- `appsettings.Production.json` in the publish output.  It sets `Viegard:WindowsService:ServiceName` to `ViegardSatelliteMDaemon`, sets `Viegard:Host:Roles` to `["sources"]`, configures PostgreSQL persistence, points the file secret provider at the local secrets directory, enables the MDaemon source, and sets `Viegard:Database:AutoMigrate` to `false`.
- `C:\Program Files\Viegard Satellite\MDaemon\secrets\viegard-db-password`, never printed and never stored in JSON.
- Windows service `ViegardSatelliteMDaemon`, display name `Viegard Satellite Pipeline (MDaemon)`, delayed automatic start, and restart recovery at 1, 5, and 15 minutes.
- Registry environment value `DOTNET_ENVIRONMENT=Production` for the service.
- ACLs granting the selected service account read access to the MDaemon logs and to the local secret file.  The secrets directory is restricted to SYSTEM, Administrators, and that service account.

After the service starts, check the Viegard admin UI `/queues` page.  The Instances table should show the new instance ID, and the Queues table should continue to show one shared row per queue.

## Upgrade

Run upgrade from the cloned repository in an elevated Windows PowerShell session:

```powershell
Set-Location C:\Viegard
.\deploy\windows\viegard-satellite.ps1 upgrade -Client MDaemon
```

The upgrade command detects the repository root from the script path, refuses to run with local git changes, fetches origin, reports incoming commits, runs `git pull --ff-only`, stops the service, republishes the app, preserves `appsettings.Production.json` and `secrets\viegard-db-password`, restarts the service, and verifies startup.  If there are no incoming commits, it exits without republishing unless `-Force` is supplied.

## Status

Status does not change the service:

```powershell
.\deploy\windows\viegard-satellite.ps1 status -Client MDaemon
```

It reports the service state, uptime when running, install directory, configured instance ID, PostgreSQL target, TCP reachability, and recent matching Application event-log entries.

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
