(() => {
    "use strict";

    function setExpanded(key, expanded) {
        const row = document.querySelector(`[data-rationale-detail="${key}"]`);
        const button = document.querySelector(`[data-rationale-toggle="${key}"]`);
        if (!row || !button) {
            return false;
        }

        row.hidden = !expanded;
        button.setAttribute("aria-expanded", expanded ? "true" : "false");
        button.setAttribute("aria-label", expanded ? "Hide rationale" : "Show rationale");
        button.textContent = expanded ? "\u2212" : "+";
        return true;
    }

    // After an Approve/Reject the server redirects to #decision-<id> for the next
    // row to review.  Open that row's detail pane automatically so the operator
    // has the full rationale without clicking the "+" toggle.
    function openFromHash() {
        const hash = window.location.hash;
        const prefix = "#decision-";
        if (!hash.startsWith(prefix) || hash.startsWith("#decision-rationale-")) {
            return;
        }

        const key = hash.slice(prefix.length);
        if (key) {
            setExpanded(key, true);
        }
    }

    document.addEventListener("DOMContentLoaded", () => {
        document.querySelectorAll("[data-rationale-toggle]").forEach(button => {
            button.addEventListener("click", () => {
                const key = button.dataset.rationaleToggle;
                if (!key) {
                    return;
                }

                const row = document.querySelector(`[data-rationale-detail="${key}"]`);
                if (!row) {
                    return;
                }

                setExpanded(key, row.hidden);
            });
        });

        openFromHash();
    });

    window.addEventListener("hashchange", openFromHash);
})();
