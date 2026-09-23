(() => {
    "use strict";

    // Progressive enhancement for the sequential review workflow.  Everything
    // here degrades safely when scripting is off: the server redirect and the
    // URL fragment already return the operator to their place in the list; this
    // script only refines focus, lets a persistent banner be dismissed, and
    // guards against a double submit.

    function focusAnchoredRow() {
        if (!window.location.hash) {
            return;
        }

        const id = window.location.hash.slice(1);
        if (!id) {
            return;
        }

        const target = document.getElementById(id);
        if (!target) {
            return;
        }

        // The native fragment jump plus scroll-margin-top already positioned the
        // row; preventScroll keeps that position while moving keyboard and
        // screen-reader focus onto the row the operator will act on next.
        target.focus({ preventScroll: true });
    }

    function wireFlashDismiss() {
        document.querySelectorAll("[data-flash-close]").forEach(button => {
            button.addEventListener("click", () => {
                const toast = button.closest(".flash-toast");
                if (toast) {
                    toast.remove();
                }
            });
        });
    }

    function wireDoubleSubmitGuard() {
        document.querySelectorAll("form.decision-review-form").forEach(form => {
            form.addEventListener("submit", () => {
                form.querySelectorAll("button[type=submit]").forEach(button => {
                    // Disable after the current submit is captured so the button
                    // value still posts; prevents a second review post from an
                    // impatient double click.
                    window.setTimeout(() => {
                        button.disabled = true;
                        button.setAttribute("aria-disabled", "true");
                    }, 0);
                });
            });
        });
    }

    document.addEventListener("DOMContentLoaded", () => {
        focusAnchoredRow();
        wireFlashDismiss();
        wireDoubleSubmitGuard();
    });
})();
