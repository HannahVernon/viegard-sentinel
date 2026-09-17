(() => {
    "use strict";

    // Shared refresh-activity indicator.  The sticky header carries a
    // revolving arrow plus an "updated Xs ago" age; auto-refreshing tables
    // carry their own age captions.  Poll scripts call
    // window.viegardRefresh.begin() before each fetch and
    // window.viegardRefresh.done(ok, captionHost) afterwards.  A single
    // one-second ticker formats every [data-refresh-host] element's age from
    // its data-refreshed-at stamp, so a failed poll is visible two ways: the
    // header indicator turns stale, and the ages keep growing.
    const indicator = document.querySelector("[data-refresh-indicator]");
    const arrow = indicator ? indicator.querySelector(".refresh-arrow") : null;
    const minSpinMs = 400;
    let inFlight = 0;
    let spinStartedAt = 0;

    function begin() {
        if (indicator) {
            indicator.hidden = false;
        }

        inFlight += 1;
        if (inFlight === 1 && arrow) {
            spinStartedAt = Date.now();
            arrow.classList.add("spinning");
        }
    }

    function done(ok, ...captionHosts) {
        if (inFlight > 0) {
            inFlight -= 1;
        }

        if (inFlight === 0 && arrow) {
            // Polls complete in tens of milliseconds; hold the spin long
            // enough to register as motion.
            const remaining = Math.max(0, minSpinMs - (Date.now() - spinStartedAt));
            setTimeout(() => {
                if (inFlight === 0) {
                    arrow.classList.remove("spinning");
                }
            }, remaining);
        }

        const now = String(Date.now());
        [indicator, ...captionHosts].forEach(host => {
            if (!host) {
                return;
            }

            if (ok) {
                host.dataset.refreshedAt = now;
                delete host.dataset.refreshStale;
            }
            else {
                host.dataset.refreshStale = "true";
            }
        });

        if (indicator) {
            indicator.classList.toggle("stale", !ok);
            indicator.title = ok
                ? "Live values refresh automatically."
                : "The last refresh failed; retrying on the next cycle.";
        }

        render();
    }

    function formatAge(refreshedAt) {
        const seconds = Math.max(0, Math.round((Date.now() - refreshedAt) / 1000));
        if (seconds < 2) {
            return "updated just now";
        }

        if (seconds < 60) {
            return "updated " + seconds + "s ago";
        }

        const minutes = Math.floor(seconds / 60);
        return "updated " + minutes + "m " + (seconds % 60) + "s ago";
    }

    function render() {
        document.querySelectorAll("[data-refresh-host]").forEach(host => {
            const ageElement = host.querySelector("[data-refresh-age]");
            if (!ageElement) {
                return;
            }

            const refreshedAt = parseInt(host.dataset.refreshedAt, 10);
            const stale = host.dataset.refreshStale === "true";
            let text = Number.isFinite(refreshedAt) ? formatAge(refreshedAt) : "";
            if (stale) {
                text = text.length > 0 ? text + " - retrying" : "refresh failed - retrying";
            }

            ageElement.textContent = text;
        });
    }

    // The server just rendered every value, so all hosts start fresh.
    const loadedAt = String(Date.now());
    document.querySelectorAll("[data-refresh-host]").forEach(host => {
        host.dataset.refreshedAt = loadedAt;
    });
    render();
    setInterval(render, 1000);

    window.viegardRefresh = { begin, done };
})();
