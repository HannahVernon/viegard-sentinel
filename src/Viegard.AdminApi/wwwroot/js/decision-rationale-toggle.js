(() => {
    "use strict";

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

                const isHidden = row.hidden;
                row.hidden = !isHidden;
                button.setAttribute("aria-expanded", isHidden ? "true" : "false");
                button.setAttribute("aria-label", isHidden ? "Hide rationale" : "Show rationale");
                button.textContent = isHidden ? "\u2212" : "+";
            });
        });
    });
})();
