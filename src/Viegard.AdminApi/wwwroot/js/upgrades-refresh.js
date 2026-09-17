(() => {
    "use strict";

    // Polls the authenticated /status/upgrades endpoint and updates the
    // recent-upgrade-commands table in place so an operator can watch a
    // requested upgrade progress without refreshing the page.  All values
    // arrive display-ready from the server; only textContent, known badge
    // class names, and hidden flags are swapped.  Transient failures are
    // ignored; the next tick tries again.  A 401 (session ended) stops the
    // timer so an expired session does not poll forever.
    const table = document.querySelector("[data-live-upgrades]");
    if (!table) {
        return;
    }

    const staleBanner = document.querySelector("[data-upgrades-stale]");
    const knownStatusClasses = ["badge", "badge badge-amber", "badge badge-blue", "badge badge-green", "badge badge-red"];
    // Poll interval comes from the same per-user preference the status dot
    // carries; clamp to the same 5-300s bounds the server enforces.
    const dot = document.querySelector("[data-status-dot]");
    const configuredMs = dot ? parseInt(dot.dataset.refreshMs, 10) : NaN;
    const intervalMs = Number.isFinite(configuredMs)
        ? Math.min(Math.max(configuredMs, 5000), 300000)
        : 30000;
    let timer = null;

    function updateCommandRows(commands) {
        if (!Array.isArray(commands)) {
            return;
        }

        const byKey = new Map();
        table.querySelectorAll("tr[data-cmd]").forEach(tr => byKey.set(tr.dataset.cmd, tr));
        const detailByKey = new Map();
        table.querySelectorAll("tr[data-cmd-detail]").forEach(tr => detailByKey.set(tr.dataset.cmdDetail, tr));

        commands.forEach(row => {
            if (!row || typeof row.key !== "string") {
                return;
            }

            const tr = byKey.get(row.key);
            if (tr) {
                const status = tr.querySelector('[data-cmd-cell="status"]');
                if (status && knownStatusClasses.includes(row.statusCss)) {
                    status.className = row.statusCss;
                    status.textContent = row.status;
                }

                ["started", "finished"].forEach(name => {
                    const cell = tr.querySelector('[data-cmd-cell="' + name + '"]');
                    if (cell && row[name] !== undefined && row[name] !== null) {
                        cell.textContent = String(row[name]);
                    }
                });
            }

            const detailRow = detailByKey.get(row.key);
            if (detailRow) {
                detailRow.hidden = row.hasDetail !== true;
                const pre = detailRow.querySelector("pre");
                if (pre && typeof row.fullDetail === "string") {
                    pre.textContent = row.fullDetail;
                }
            }
        });
    }

    async function refresh() {
        const rf = window.viegardRefresh || { begin() { }, done() { } };
        const caption = document.querySelector('[data-refresh-host="upgrades"]');
        rf.begin();
        let ok = false;
        try {
            const response = await fetch("/status/upgrades", { headers: { "Accept": "application/json" } });
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

            updateCommandRows(data.commands);
            if (staleBanner) {
                staleBanner.hidden = data.stalePending !== true;
            }

            ok = true;
        } catch {
            // Network hiccup (including the admin container restarting
            // mid-upgrade); leave the last known state visible.
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
