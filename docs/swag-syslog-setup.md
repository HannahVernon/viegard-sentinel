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
  # Fail-closed allowlist: the SWAG host's address as seen by Viegard.
  Viegard__Sources__Syslog__AllowedSources__0: "192.0.2.10"
ports:
  - "5514:5514/udp"
```

Also restrict UDP 5514 to the SWAG host in the Docker host's firewall;
Viegard's allowlist is the second layer, not the only one.

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
- Lines tagged `nginx_access` are parsed into structured HTTP request
  events; anything unparseable (and `nginx_error` lines, for now) becomes a
  generic syslog event, so no data is lost to parser gaps.
- UDP delivery is lossy by nature; the SWAG file logs remain the source of
  truth for forensics.
