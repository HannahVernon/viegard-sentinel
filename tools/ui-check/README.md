# UI screenshot harness (tools/ui-check)

Screenshots every admin navigation destination at a phone viewport (iPhone
13) and a desktop viewport (1600x900) so layout changes are verified by
looking at rendered pages instead of reasoning about CSS.  Use it before any
PR that touches layout, navigation, or page structure.

**Local throwaway instances only.**  The harness types passwords from
environment variables and mutates account state (bootstrap password change,
TOTP enrollment).  Never point it at a real deployment.

## Prerequisites

- Node.js 20 or later.
- One-time setup in this directory:

```powershell
npm ci
npx playwright install chromium
```

## Start a local admin instance

Follow the "Empty admin UI with in-memory persistence" recipe in
`docs/local-development.md` (throwaway secrets directory plus a loopback
Kestrel on `http://127.0.0.1:8080`).  The harness defaults match that
recipe's bootstrap username and expects the bootstrap password below.

## Run

```powershell
node ui-check.mjs          # both profiles
node ui-check.mjs mobile   # phone viewport only
node ui-check.mjs desktop  # desktop viewport only
```

Screenshots land in `shots/mobile/` and `shots/desktop/` (gitignored).
The mobile profile additionally captures the opened navigation menu, and
both profiles capture the `/configuration#upgrades` section with the
sticky section index.

## Environment variables

Variable | Default | Meaning
---------|---------|--------
`UI_CHECK_BASE` | `http://127.0.0.1:8080` | Base URL of the local instance
`UI_CHECK_USER` | `admin` | Bootstrap username
`UI_CHECK_BOOTSTRAP_PASSWORD` | `local-mobile-check-throwaway1` | The value written to the local secrets file
`UI_CHECK_PASSWORD` | `local-mobile-check-throwaway2-Xy` | Password set during the forced bootstrap change

## Repeat runs and state

On first contact with a fresh instance the harness performs the bootstrap
password change and TOTP enrollment, persisting the enrolled secret and
effective password in `shots/.state.json` (gitignored) so repeat runs
against the same still-running instance log straight in with a computed
TOTP code.  Restarting the instance resets its in-memory state; delete
`shots/.state.json` to match, or the harness will tell you to.
