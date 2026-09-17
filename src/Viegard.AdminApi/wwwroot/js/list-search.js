(() => {
    "use strict";

    // Static SSR gives no feedback between submitting a search and the
    // results page arriving, and long searches run up to a 60-second
    // budget.  Show progress on the form itself and spin the header
    // refresh arrow for the duration of the navigation.
    const forms = Array.from(document.querySelectorAll("form.filter-bar"));
    if (forms.length === 0) {
        return;
    }

    forms.forEach(form => {
        form.addEventListener("submit", () => {
            const button = form.querySelector("button[type=submit]");
            if (button) {
                button.dataset.originalText = button.textContent ?? "";
                button.disabled = true;
                button.textContent = "Searching\u2026";
            }

            const rf = window.viegardRefresh;
            if (rf && typeof rf.begin === "function") {
                rf.begin();
            }
        });
    });

    // Back/forward-cache restores keep the mutated DOM; put the button back.
    window.addEventListener("pageshow", event => {
        if (!event.persisted) {
            return;
        }

        forms.forEach(form => {
            const button = form.querySelector("button[type=submit]");
            if (button && button.dataset.originalText !== undefined) {
                button.disabled = false;
                button.textContent = button.dataset.originalText;
            }
        });
    });
})();
