# Sending SWAG/nginx Logs to Viegard via Syslog

Viegard ingests nginx logs through its syslog UDP listener (D-0023).  nginx
logs natively to syslog over UDP; SWAG keeps its file logs enabled as the
durable record, so syslog is an additional real-time feed, not a replacement.

All host names and addresses below are placeholders; substitute your own.

## 1. Enable the Viegard listener

In the pipeline host configuration (environment variables shown for a
container deployment):

```yaml
environment:
  Viegard__Sources__Syslog__Enabled: "true"
  Viegard__Sources__Syslog__Port: "5514"
  # Fail-closed allowlist: single addresses and/or CIDR ranges.
  Viegard__Sources__Syslog__AllowedSources__0: "192.0.2.10"
  # e.g. also allow a whole LAN range:
  # Viegard__Sources__Syslog__AllowedSources__1: "192.168.0.0/16"
ports:
  - "5514:5514/udp"
```

Also restrict UDP 5514 to the SWAG host in the Docker host's firewall (the
`DOCKER-USER` chain; see "Firewalling the syslog port" in
[deployment.md](deployment.md)); Viegard's allowlist is the second layer, not the
only one.  Place the SWAG host and the Viegard host on a dedicated VLAN, because
UDP source addresses can be forged.

The listener binds `Viegard__Sources__Syslog__ListenAddress`, default
`0.0.0.0` (all interfaces in the container).  Publish the port on a single
host address, or set `ListenAddress`, to narrow that.

Add your own public addresses to the protected list so automation never bans
them: `viegard-deploy.sh configure --protected-cidrs <cidr,...>` (see
deployment.md).

## 2. Add the Viegard log format to SWAG's nginx

In the SWAG container, edit `/config/nginx/nginx.conf` and add to the `http`
block (the trailing `host=`/`rt=` fields are Viegard extensions; the parser
also accepts plain combined format):

```nginx
log_format viegard '$remote_addr - $remote_user [$time_local] "$request" '
                   '$status $body_bytes_sent "$http_referer" "$http_user_agent" '
                   'host=$host rt=$request_time';

access_log syslog:server=192.0.2.20:5514,facility=local7,tag=nginx_access,severity=info viegard;
error_log  syslog:server=192.0.2.20:5514,facility=local7,tag=nginx_error warn;
```

`192.0.2.20` is the Viegard Docker host.  Keep the existing file-based
`access_log`/`error_log` directives; nginx supports multiple targets.

Reload nginx inside SWAG afterwards (`nginx -s reload` or restart the
container).

## 3. Behavior notes

- Datagrams from addresses not on `AllowedSources` are dropped and counted
  (visible in the source's health detail).
- Oversized datagrams (default > 8192 bytes) and datagrams beyond the
  per-source rate cap (default 500/s) are dropped.
- The remote address of every `nginx_access` line must be a literal IPv4 or
  IPv6 address.  It is canonicalised (IPv4-mapped IPv6 becomes IPv4) and a
  line with any other value is dropped, not stored, and counted (metric
  `viegard.syslog.nginx_malformed_address_dropped`).
- There is no shared-token option: nginx's syslog output cannot add a
  secret without changing the tag that the parser matches, and a token in a
  UDP datagram is readable by anyone who can see the traffic.  VLAN and
  firewall isolation are the controls (D-0083).
- Lines tagged `nginx_access` are parsed into structured HTTP request
  events; anything unparseable (and `nginx_error` lines, for now) becomes a
  generic syslog event, so no data is lost to parser gaps.
- UDP delivery is lossy by nature; the SWAG file logs remain the source of
  truth for forensics.
