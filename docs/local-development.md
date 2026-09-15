# Local development on Windows

## Assumptions

- You are running commands from the repository root on Windows PowerShell.  Paths in commands use Windows separators.
- PostgreSQL is optional for an empty admin UI smoke test, but it is required if the PipelineHost and AdminApi should share data.
- These steps use the **file secret provider** with a local throwaway directory, matching the production pattern.

## Local secrets

Create a directory with one file per secret (contents are the secret values; local throwaways only):

```powershell
New-Item -ItemType Directory -Path C:\temp\viegard-local-secrets -Force | Out-Null
Set-Content -Path C:\temp\viegard-local-secrets\viegard-db-password -Value 'replace-with-local-password' -NoNewline
Set-Content -Path C:\temp\viegard-local-secrets\viegard-admin-bootstrap-password -Value 'replace-with-a-20-character-local-password' -NoNewline
```

## Empty admin UI with in-memory persistence

This mode verifies that the static SSR admin pages render on loopback with empty stores.  Pipeline data is not shared between processes in this mode.

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:ASPNETCORE_URLS = "http://127.0.0.1:8080"
$env:Viegard__Persistence__Provider = "inmemory"
$env:Viegard__Secrets__Provider = "file"
$env:Viegard__Secrets__Directory = "C:\temp\viegard-local-secrets"
$env:Viegard__Admin__Exposure = "loopback"
$env:Viegard__Admin__Bootstrap__Username = "admin"
$env:Viegard__Admin__Bootstrap__PasswordSecretName = "viegard-admin-bootstrap-password"
dotnet run --project .\src\Viegard.AdminApi --no-launch-profile
```

Open `http://127.0.0.1:8080` or the URL printed by Kestrel.  The admin pages should load after bootstrap login, password change, and TOTP enrollment.

## Shared local PostgreSQL for end-to-end data

Start the existing WSL Docker PostgreSQL container **and keep a WSL session alive**: the WSL VM idles out shortly after the last session exits, taking Docker's port forward with it.  Leave this running in its own window:

```powershell
wsl -d Debian -u root -- sh -c "service docker start >/dev/null 2>&1; docker start viegard-test-pg >/dev/null 2>&1; exec sleep infinity"
```

Use `Host=127.0.0.1`, not `localhost`.  The WSL Docker port forward is IPv4-only on this workstation, and `localhost` can resolve to IPv6 first.

Configure both projects with the same database settings via user-secrets (these are ordinary configuration values; the passwords live in the secrets directory above):

```powershell
foreach ($proj in '.\src\Viegard.PipelineHost', '.\src\Viegard.AdminApi') {
    dotnet user-secrets set --project $proj "Viegard:Secrets:Provider" "file"
    dotnet user-secrets set --project $proj "Viegard:Secrets:Directory" "C:\temp\viegard-local-secrets"
    dotnet user-secrets set --project $proj "Viegard:Persistence:Provider" "postgres"
    dotnet user-secrets set --project $proj "Viegard:Database:Host" "127.0.0.1"
    dotnet user-secrets set --project $proj "Viegard:Database:Port" "5433"
    dotnet user-secrets set --project $proj "Viegard:Database:Name" "viegard_test"
    dotnet user-secrets set --project $proj "Viegard:Database:Username" "viegard_test"
    dotnet user-secrets set --project $proj "Viegard:Database:Schema" "viegard"
    dotnet user-secrets set --project $proj "Viegard:Database:PasswordSecretName" "viegard-db-password"
}
dotnet user-secrets set --project .\src\Viegard.AdminApi "Viegard:Admin:Exposure" "loopback"
dotnet user-secrets set --project .\src\Viegard.AdminApi "Viegard:Admin:Bootstrap:Username" "admin"
dotnet user-secrets set --project .\src\Viegard.AdminApi "Viegard:Admin:Bootstrap:PasswordSecretName" "viegard-admin-bootstrap-password"
```

Run the PipelineHost in one PowerShell window.  This enables syslog on loopback and all singleton processing roles for local testing.

```powershell
$env:DOTNET_ENVIRONMENT = "Development"
$env:Viegard__Host__InstanceId = "local-pipeline"
$env:Viegard__Host__Roles__0 = "sources"
$env:Viegard__Host__Roles__1 = "correlation"
$env:Viegard__Host__Roles__2 = "classification"
$env:Viegard__Host__Roles__3 = "policy"
$env:Viegard__Sources__Syslog__Enabled = "true"
$env:Viegard__Sources__Syslog__ListenAddress = "127.0.0.1"
$env:Viegard__Sources__Syslog__Port = "5514"
$env:Viegard__Sources__Syslog__AllowedSources__0 = "127.0.0.1"
dotnet run --project .\src\Viegard.PipelineHost
```

Run the AdminApi in another PowerShell window.

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:ASPNETCORE_URLS = "http://127.0.0.1:8080"
dotnet run --project .\src\Viegard.AdminApi --no-launch-profile
```

## Send sample syslog datagrams

The following samples use RFC 5737 example IP addresses and `example.com`.  One request includes `ref=aftership` so the seeded D-0029 signature can fire end to end.

```powershell
$client = [System.Net.Sockets.UdpClient]::new()
$endpoint = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Parse("127.0.0.1"), 5514)
$lines = @(
    '<134>Sep 14 08:40:01 example-host nginx_access: 203.0.113.10 - - [14/Sep/2026:08:40:01 -0500] "GET / HTTP/1.1" 200 512 "-" "Mozilla/5.0" host=www.example.com rt=0.010',
    '<134>Sep 14 08:40:02 example-host nginx_access: 203.0.113.11 - - [14/Sep/2026:08:40:02 -0500] "GET /track?order=42&ref=aftership HTTP/1.1" 200 640 "https://www.example.com/" "Mozilla/5.0" host=www.example.com rt=0.014',
    '<134>Sep 14 08:40:03 example-host nginx_access: 203.0.113.12 - - [14/Sep/2026:08:40:03 -0500] "GET /.env HTTP/1.1" 404 32 "-" "curl/8.0" host=www.example.com rt=0.006',
    '<134>Sep 14 08:40:04 example-host nginx_access: 203.0.113.13 - - [14/Sep/2026:08:40:04 -0500] "GET /search?q=%27%20or%201=1 HTTP/1.1" 404 48 "-" "sqlmap/1.7" host=www.example.com rt=0.020'
)
foreach ($line in $lines) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($line)
    [void]$client.Send($bytes, $bytes.Length, $endpoint)
}
$client.Dispose()
```

After a few seconds, check `/events`, `/incidents`, `/decisions`, `/audit`, `/queues`, and `/signatures`.  The aftership request should produce custom-signature evidence, then deterministic classification and a dry-run policy decision.
