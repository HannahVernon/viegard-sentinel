# Read-only API

Viegard exposes a read-only JSON API (D-0040) so trusted automation can
inspect platform state without an interactive session.  Every endpoint is
GET-only: nothing on this surface can change state, and app-password
principals can never satisfy the step-up gate that guards mutations.

## Authentication

Create an app password on **/account → App passwords** (step-up required).
The token is shown exactly once.  Send it as a bearer token:

```
curl -H "Authorization: Bearer viegard_ro_..." https://viegard.example.com/api/v1/decisions
```

Interactive cookie sessions can also call these endpoints.  Requests are
subject to the same source-address allowlist as the rest of the admin
interface, and tokens expire after the configured lifetime (90 days by
default) or on revocation from the account page.

## Endpoints

Path | Returns
-----|--------
`GET /api/v1/events` | Normalized events (payloads carry a `$payloadType` discriminator)
`GET /api/v1/events/{id}` | One event
`GET /api/v1/incidents` | Incidents
`GET /api/v1/incidents/{id}` | One incident
`GET /api/v1/decisions` | Policy decisions
`GET /api/v1/decisions/{id}` | One decision with its classification embedded
`GET /api/v1/classifications/{id}` | One classification
`GET /api/v1/advisor/summary` | Local-model advisor configuration (enabled, endpoint, model, temperature, timeout, invocation band, clamp deltas, response cache enabled/TTL, settings version), per-category `categoryOverrides`, `activePromptTemplate`, and outcome counts, cache hits, escalation rate, failures, and latency over 1h/24h/7d/all-time windows
`GET /api/v1/advisor/consults` | Local-model advisor consult records (append-only observability rows)
`GET /api/v1/advisor/consults/{id}` | One advisor consult record
`GET /api/v1/advisor/consults/by-classification/{id}` | The advisor consult recorded for a classification, if any
`GET /api/v1/audit` | Audit ledger records
`GET /api/v1/bans` | Active bans and the 50 most recent ban actions
`GET /status/queues` | Queue and instance health (display-formatted)
`GET /status/upgrades` | Recent host upgrade commands (display-formatted)
`GET /status/bans` | Bans (display-formatted for the live UI)

## List parameters

Parameter | Meaning
----------|--------
`take` | Page size, 1-200 (default 50)
`cursor` | Keyset cursor; use `nextCursor` from the previous page
`q` | Text search (`term "a phrase" OR other -exclude`)
`sort`, `dir` | Column key and `asc`/`desc`; same keys as the UI list pages
`state` | Incidents only: incident state name
`outcome` | Decisions only: decision outcome name (for example `RequireApproval`)
`minSeverity`, `maxSeverity` | Decisions only: classification severity bounds (1-10)
`unreviewed` | Decisions only: `1` restricts to unreviewed decisions
`stage` | Audit only: pipeline stage name
`outcome` | Advisor consults also accept an outcome name (`Escalated`, `NoChange`, `ProviderFailed`, `InvalidOutput`, `SkippedOutOfBand`)

List responses share one shape:

```json
{ "items": [ ... ], "nextCursor": "guid-or-null", "totalCount": 123, "preceding": 0 }
```

Timestamps are ISO 8601 (UTC), enums are strings.  A text search that
exceeds the shared search budget answers `408` with an explanatory `error`
field; unknown ids answer `404`.

`GET /api/v1/advisor/summary` returns `categoryOverrides` as an array of
per-category advisor override rows.  Each row includes `category`,
nullable `enabled`, nullable invocation-band and clamp fields, `version`,
and `updatedAt`; null override fields inherit the global configuration.

The summary `configuration` object includes `responseCacheEnabled` and
`responseCacheTtlHours`.  Each window includes `cacheHits`, counted from
consult records where the real outcome remains `Escalated` or `NoChange`
and `servedFromCache` is true.

The same response includes `activePromptTemplate` with `templateId`, `revision`, and `createdAt` for the active database prompt revision, or `null` before the prompt seed exists.
