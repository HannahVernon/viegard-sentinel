(() => {
    "use strict";

    // Polls the authenticated queue-status endpoint and updates the header
    // dot in place (D-0016 staleness auto-refresh).  On /queues it also
    // updates the telemetry tables and retention card values in place (all
    // values arrive display-ready from the server; only textContent and
    // known class names are swapped).  Transient failures are ignored; the
    // next tick tries again.  A 401 (session ended) stops the timer so an
    // expired session does not poll forever.
    const dot = document.querySelector("[data-status-dot]");
    const label = document.querySelector("[data-status-label]");
    if (!dot || !label) {
        return;
    }

    const knownClasses = ["status-dot-green", "status-dot-amber", "status-dot-red", "status-dot-muted"];
    const knownLightClasses = ["badge", "badge badge-green", "badge badge-amber", "badge badge-red"];
    const liveTable = document.querySelector("[data-live-queues]");
    const liveInstances = document.querySelector("[data-live-instances]");
    const retentionLastCycle = document.querySelector("[data-retention-last-cycle]");
    const retentionTotal = document.querySelector("[data-retention-total]");
    // Poll interval comes from the per-user preference rendered on the dot;
    // clamp to the same 5-300s bounds the server enforces.
    const configuredMs = parseInt(dot.dataset.refreshMs, 10);
    const intervalMs = Number.isFinite(configuredMs)
        ? Math.min(Math.max(configuredMs, 5000), 300000)
        : 30000;
    let timer = null;

    function updateQueueRows(rows) {
        if (!liveTable || !Array.isArray(rows)) {
            return;
        }

        const byKey = new Map();
        liveTable.querySelectorAll("tr[data-q]").forEach(tr => byKey.set(tr.dataset.q, tr));
        const reasonsByKey = new Map();
        liveTable.querySelectorAll("tr[data-q-reasons]").forEach(tr => reasonsByKey.set(tr.dataset.qReasons, tr));

        rows.forEach(row => {
            if (!row || typeof row.key !== "string") {
                return;
            }

            const tr = byKey.get(row.key);
            if (tr) {
                const light = tr.querySelector('[data-q-cell="light"]');
                if (light && knownLightClasses.includes(row.lightCss)) {
                    light.className = row.lightCss;
                    light.textContent = row.light;
                }

                ["depth", "inFlight", "oldestAge", "deadLetters", "totals", "captured"].forEach(name => {
                    const cell = tr.querySelector('[data-q-cell="' + name + '"]');
                    if (cell && row[name] !== undefined && row[name] !== null) {
                        cell.textContent = String(row[name]);
                    }
                });
            }

            const reasonsRow = reasonsByKey.get(row.key);
            if (reasonsRow) {
                reasonsRow.hidden = row.green === true || !row.reasons;
                const cell = reasonsRow.querySelector("td");
                if (cell) {
                    cell.textContent = row.reasons || "";
                }
            }
        });
    }

    function updateInstanceRows(instances) {
        if (!liveInstances || !Array.isArray(instances)) {
            return;
        }

        const byKey = new Map();
        liveInstances.querySelectorAll("tr[data-inst]").forEach(tr => byKey.set(tr.dataset.inst, tr));
        const reasonsByKey = new Map();
        liveInstances.querySelectorAll("tr[data-inst-reasons]").forEach(tr => reasonsByKey.set(tr.dataset.instReasons, tr));

        instances.forEach(row => {
            if (!row || typeof row.key !== "string") {
                return;
            }

            const tr = byKey.get(row.key);
            if (tr) {
                const light = tr.querySelector('[data-inst-cell="light"]');
                if (light && knownLightClasses.includes(row.lightCss)) {
                    light.className = row.lightCss;
                    light.textContent = row.light;
                }

                ["queuesReported", "lastCaptured"].forEach(name => {
                    const cell = tr.querySelector('[data-inst-cell="' + name + '"]');
                    if (cell && row[name] !== undefined && row[name] !== null) {
                        cell.textContent = String(row[name]);
                    }
                });
            }

            const reasonsRow = reasonsByKey.get(row.key);
            if (reasonsRow) {
                reasonsRow.hidden = row.green === true || !row.reasons;
                const cell = reasonsRow.querySelector("td");
                if (cell) {
                    cell.textContent = row.reasons || "";
                }
            }
        });
    }

    function updateRetention(retention) {
        if (!retention) {
            return;
        }

        if (retentionLastCycle && typeof retention.lastCycle === "string") {
            retentionLastCycle.textContent = retention.lastCycle;
        }

        if (retentionTotal && retention.lastRowsRemoved !== undefined && retention.lastRowsRemoved !== null) {
            retentionTotal.textContent = String(retention.lastRowsRemoved);
        }
    }

    async function refresh() {
        try {
            const response = await fetch("/status/queues", { headers: { "Accept": "application/json" } });
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
            if (!data || !knownClasses.includes(data.css)) {
                return;
            }

            knownClasses.forEach(name => dot.classList.remove(name));
            dot.classList.add(data.css);
            label.textContent = data.label || "";
            updateQueueRows(data.rows);
            updateInstanceRows(data.instances);
            updateRetention(data.retention);
        } catch {
            // Network hiccup; leave the last known state visible.
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
