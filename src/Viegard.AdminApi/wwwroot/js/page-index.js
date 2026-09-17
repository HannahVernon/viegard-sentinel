(() => {
    "use strict";

    // Marks the page-index link for the currently visible section with
    // aria-current="location".  Fragments never reach the server, so static
    // SSR cannot render the active state; this replaces a hand-maintained
    // CSS :has(:target) selector list that had drifted out of date as
    // sections were added.
    const links = Array.from(document.querySelectorAll(".page-index a"));
    if (links.length === 0) {
        return;
    }

    function apply() {
        const hash = window.location.hash;
        let current = null;
        if (hash.length > 1) {
            current = links.find(link => link.hash === hash) ?? null;
        }

        // No fragment (or an unknown one): the default section is visible.
        current ??= links.find(link => link.classList.contains("index-default-link")) ?? null;

        links.forEach(link => {
            if (link === current) {
                link.setAttribute("aria-current", "location");
            }
            else {
                link.removeAttribute("aria-current");
            }
        });
    }

    window.addEventListener("hashchange", apply);
    apply();
})();
