(() => {
    "use strict";

    // Polls the authenticated queue-status endpoint and updates the header
    // dot in place (D-0016 staleness auto-refresh).  Transient failures are
    // ignored; the next tick tries again.  A 401 (session ended) stops the
    // timer so an expired session does not poll forever.
    const dot = document.querySelector("[data-status-dot]");
    const label = document.querySelector("[data-status-label]");
    if (!dot || !label) {
        return;
    }

    const knownClasses = ["status-dot-green", "status-dot-amber", "status-dot-red", "status-dot-muted"];
    let timer = null;

    async function refresh() {
        try {
            const response = await fetch("/status/queues", { headers: { "Accept": "application/json" } });
            if (response.status === 401 || response.redirected) {
                // Session ended: the cookie handler answers with a login
                // redirect rather than a bare 401.  Stop polling either way.
                if (timer) {
                    clearInterval(timer);
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
        } catch {
            // Network hiccup; leave the last known state visible.
        }
    }

    timer = setInterval(refresh, 30000);
})();
