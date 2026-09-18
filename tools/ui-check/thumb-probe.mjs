// Synthetic-thumb probe of the mobile section-index chip strip: real touch
// flicks via CDP Input.synthesizeScrollGesture, plus active-chip visibility
// checks at both ends of the strip.
import { chromium, devices } from "playwright";
import { createRequire } from "node:module";
import { readFileSync, writeFileSync, existsSync, mkdirSync } from "node:fs";

const { generateSync } = createRequire(import.meta.url)("otplib");
const BASE = "http://127.0.0.1:8080";
mkdirSync("shots/mobile", { recursive: true });
const state = existsSync("shots/.state.json") ? JSON.parse(readFileSync("shots/.state.json", "utf8")) : {};

const browser = await chromium.launch();
const context = await browser.newContext({ ...devices["iPhone 13"] });
const page = await context.newPage();

// login (fresh instance: full bootstrap; otherwise state relogin)
await page.goto(`${BASE}/login`);
await page.fill('input[name="username"]', "admin");
await page.fill('input[name="password"]', state.password ?? "local-mobile-check-throwaway1");
await Promise.all([page.waitForLoadState("networkidle"), page.click('button[type="submit"]')]);
if (page.url().includes("/login")) {
    if (await page.locator('input[name="newPassword"]').count() > 0) {
        await page.fill('input[name="newPassword"]', "local-mobile-check-throwaway2-Xy");
        await page.fill('input[name="confirmPassword"]', "local-mobile-check-throwaway2-Xy");
        await Promise.all([page.waitForLoadState("networkidle"), page.click('button[type="submit"]')]);
    }
    if (await page.locator("pre.secret-block").count() > 0) {
        state.secret = (await page.locator("pre.secret-block").first().textContent()).replace(/\s+/g, "");
    }
    if (await page.locator('input[name="code"]').count() > 0) {
        const r = generateSync({ secret: state.secret });
        await page.fill('input[name="code"]', typeof r === "string" ? r : (r.value ?? r.token));
        await Promise.all([page.waitForLoadState("networkidle"), page.click('button[type="submit"]')]);
    }
    state.password = state.password ?? "local-mobile-check-throwaway2-Xy";
    writeFileSync("shots/.state.json", JSON.stringify(state));
}
console.log("logged in:", page.url());

async function stripMetrics(label) {
    const m = await page.evaluate(() => {
        const strip = document.querySelector(".page-index");
        const active = strip?.querySelector('a[aria-current="location"]');
        const stripBox = strip?.getBoundingClientRect();
        const activeBox = active?.getBoundingClientRect();
        return {
            scrollLeft: Math.round(strip?.scrollLeft ?? -1),
            scrollWidth: strip?.scrollWidth ?? -1,
            clientWidth: strip?.clientWidth ?? -1,
            chipHeight: Math.round(strip?.querySelector("a")?.getBoundingClientRect().height ?? -1),
            activeText: active?.textContent?.trim() ?? "(none)",
            activeVisible: active && activeBox.left >= stripBox.left - 1 && activeBox.right <= stripBox.right + 1,
            activePartial: active && !(activeBox.left >= stripBox.left - 1 && activeBox.right <= stripBox.right + 1)
                && activeBox.right > stripBox.left && activeBox.left < stripBox.right,
        };
    });
    console.log(label, JSON.stringify(m));
    return m;
}

// Case 1: land on the FIRST section - active chip should be fully visible.
await page.goto(`${BASE}/configuration`);
await page.waitForSelector(".admin-header");
await page.waitForTimeout(300); // let page-index.js stamp aria-current
await stripMetrics("case1 (default/Retention):");

// Case 2: land on the LAST section via fragment - is the active chip visible?
await page.goto(`${BASE}/configuration#ingestion`);
await page.waitForSelector(".admin-header");
await page.waitForTimeout(300);
await page.screenshot({ path: "shots/mobile/50-chips-ingestion-load.png" });
await stripMetrics("case2 (land on #ingestion):");

// Case 3: the thumb - flick the strip left with a synthesized touch gesture.
const client = await context.newCDPSession(page);
const strip = await page.locator(".page-index").boundingBox();
await client.send("Input.synthesizeScrollGesture", {
    x: Math.round(strip.x + strip.width * 0.7),
    y: Math.round(strip.y + strip.height / 2),
    xDistance: -250,
    yDistance: 0,
    speed: 1200,
    gestureSourceType: "touch",
});
await page.waitForTimeout(400);
await page.screenshot({ path: "shots/mobile/51-chips-after-flick.png" });
await stripMetrics("case3 (after touch flick left):");

// Case 4: flick back right.
await client.send("Input.synthesizeScrollGesture", {
    x: Math.round(strip.x + strip.width * 0.3),
    y: Math.round(strip.y + strip.height / 2),
    xDistance: 250,
    yDistance: 0,
    speed: 1200,
    gestureSourceType: "touch",
});
await page.waitForTimeout(400);
await stripMetrics("case4 (after flick back):");

// Case 5: does a vertical page swipe accidentally scroll the strip?
await client.send("Input.synthesizeScrollGesture", {
    x: 200,
    y: 500,
    xDistance: 0,
    yDistance: -300,
    speed: 1200,
    gestureSourceType: "touch",
});
await page.waitForTimeout(400);
await stripMetrics("case5 (after vertical page swipe):");
await page.screenshot({ path: "shots/mobile/52-after-vertical-swipe.png" });

// Case 6: discriminator - if JS scrollBy moves it, overflow-x works and the
// failed flick was gesture-side; if not, the CSS never applied.
const diag = await page.evaluate(() => {
    const s = document.querySelector(".page-index");
    const cs = getComputedStyle(s);
    s.scrollBy({ left: 200 });
    return {
        overflowX: cs.overflowX,
        position: cs.position,
        display: cs.display,
        touchAction: cs.touchAction,
        scrollLeftAfterJs: Math.round(s.scrollLeft),
    };
});
console.log("case6 (computed style + JS scroll):", JSON.stringify(diag));

await browser.close();
console.log("done");
