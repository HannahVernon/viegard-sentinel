(() => {
    "use strict";

    // Polls the authenticated /status/bans endpoint and rebuilds the active
    // bans and recent ban actions tables so an operator can watch new bans
    // arrive without refreshing the page.  All values arrive display-ready
    // from the server.  Rows are cloned from server-rendered <template>
    // elements (which carry the antiforgery token for the unban form), and
    // values are assigned via textContent only.  Transient failures are
    // ignored; the next tick tries again.  A 401 (session ended) stops the
    // timer so an expired session does not poll forever.
    const bansTable = document.querySelector("[data-live-bans]");
    const actionsTable = document.querySelector("[data-live-ban-actions]");
    if (!bansTable || !actionsTable) {
        return;
    }

    const bansEmpty = document.querySelector("[data-bans-empty]");
    const actionsEmpty = document.querySelector("[data-ban-actions-empty]");
    const banTemplate = document.querySelector("[data-ban-row-template]");
    const actionTemplate = document.querySelector("[data-ban-action-row-template]");
    const knownStatusClasses = ["badge", "badge badge-amber", "badge badge-blue", "badge badge-green", "badge badge-red"];
    const decisionIdShape = /^[0-9a-f]{32}$/;
    // Poll interval comes from the same per-user preference the status dot
    // carries; clamp to the same 5-300s bounds the server enforces.
    const dot = document.querySelector("[data-status-dot]");
    const configuredMs = dot ? parseInt(dot.dataset.refreshMs, 10) : NaN;
    const intervalMs = Number.isFinite(configuredMs)
        ? Math.min(Math.max(configuredMs, 5000), 300000)
        : 30000;
    let timer = null;

    function setCell(row, name, value) {
        const cell = row.querySelector('[data-cell="' + name + '"]');
        if (cell && typeof value === "string") {
            cell.textContent = value;
        }

        return cell;
    }

    function setDecisionLink(row, id, shortId) {
        const link = setCell(row, "decision", typeof shortId === "string" ? shortId : "");
        if (link && typeof id === "string" && decisionIdShape.test(id)) {
            link.setAttribute("href", "/decisions/" + id);
        }
    }

    function rebuildBans(bans) {
        if (!Array.isArray(bans) || !banTemplate) {
            return;
        }

        const body = bansTable.querySelector("tbody");
        if (!body) {
            return;
        }

        const rows = bans.map(ban => {
            const fragment = banTemplate.content.cloneNode(true);
            const row = fragment.querySelector("tr");
            if (!row || !ban || typeof ban.ip !== "string") {
                return null;
            }

            setCell(row, "ip", ban.ip);
            setCell(row, "expires", ban.expiresIn);
            setCell(row, "created", ban.created);
            setDecisionLink(row, ban.decisionId, ban.decisionShort);
            const ipInput = row.querySelector('input[name="ip"]');
            if (ipInput) {
                ipInput.value = ban.ip;
            }

            return row;
        }).filter(row => row !== null);

        body.replaceChildren(...rows);
        bansTable.hidden = rows.length === 0;
        if (bansEmpty) {
            bansEmpty.hidden = rows.length > 0;
        }
    }

    function rebuildActions(actions) {
        if (!Array.isArray(actions) || !actionTemplate) {
            return;
        }

        const body = actionsTable.querySelector("tbody");
        if (!body) {
            return;
        }

        const rows = actions.map(action => {
            const fragment = actionTemplate.content.cloneNode(true);
            const row = fragment.querySelector("tr");
            if (!row || !action) {
                return null;
            }

            setCell(row, "operation", action.operation);
            const status = setCell(row, "status", action.status);
            if (status && knownStatusClasses.includes(action.statusCss)) {
                status.className = action.statusCss;
            }

            setCell(row, "requested", action.requested);
            setCell(row, "completed", action.completed);
            setCell(row, "summary", action.summary);
            setDecisionLink(row, action.decisionId, action.decisionShort);
            return row;
        }).filter(row => row !== null);

        body.replaceChildren(...rows);
        actionsTable.hidden = rows.length === 0;
        if (actionsEmpty) {
            actionsEmpty.hidden = rows.length > 0;
        }
    }

    async function refresh() {
        const rf = window.viegardRefresh || { begin() { }, done() { } };
        const caption = document.querySelector('[data-refresh-host="bans"]');
        rf.begin();
        let ok = false;
        try {
            const response = await fetch("/status/bans", { headers: { "Accept": "application/json" } });
            if (response.status === 401 || response.redirected) {
                // Session ended: the cookie handler answers with a login
                // redirect rather than a bare 401.  Stop polling either way.
                if (timer) {
                    clearInterval(timer);
                    timer = null;
                }

                return;
            }

            if (!response.ok) {
                return;
            }

            const data = await response.json();
            if (!data) {
                return;
            }

            rebuildBans(data.activeBans);
            rebuildActions(data.recentActions);
            ok = true;
        } catch {
            // Network hiccup; leave the last known state visible.
        } finally {
            if (caption) {
                rf.done(ok, caption);
            }
            else {
                rf.done(ok);
            }
        }
    }

    timer = setInterval(refresh, intervalMs);

    // Background tabs get throttled timers, so an overdue tick can leave
    // stale values visible for a few seconds after switching back.  Refresh
    // immediately when the tab becomes visible again (unless polling was
    // stopped because the session ended).
    document.addEventListener("visibilitychange", () => {
        if (document.visibilityState === "visible" && timer) {
            refresh();
        }
    });
})();
