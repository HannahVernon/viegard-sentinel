// UI screenshot harness for the Viegard admin interface (tools/ui-check).
//
// Drives a LOCAL, THROWAWAY admin instance (inmemory persistence per
// docs/local-development.md) through the real bootstrap login, password
// change, and TOTP enrollment, then screenshots every navigation
// destination at phone and desktop viewports.  Never point this at a real
// deployment: it types passwords from environment variables and mutates
// account state.
//
// Usage:  node ui-check.mjs [all|mobile|desktop]
// Output: shots/<profile>/NN-page.png
//
// The enrolled TOTP secret and effective password persist in
// shots/.state.json (gitignored) so repeat runs against the same still-
// running instance can log straight in.  Restarting the instance resets its
// inmemory state; delete shots/.state.json to match.
import { chromium, devices } from "playwright";
import { createRequire } from "node:module";
import { mkdirSync, readFileSync, writeFileSync, existsSync } from "node:fs";

const { generateSync } = createRequire(import.meta.url)("otplib");

const BASE = process.env.UI_CHECK_BASE ?? "http://127.0.0.1:8080";
const USER = process.env.UI_CHECK_USER ?? "admin";
const BOOTSTRAP_PASSWORD = process.env.UI_CHECK_BOOTSTRAP_PASSWORD ?? "local-mobile-check-throwaway1";
const NEW_PASSWORD = process.env.UI_CHECK_PASSWORD ?? "local-mobile-check-throwaway2-Xy";
const STATE_PATH = "shots/.state.json";

const profiles = {
    mobile: { ...devices["iPhone 13"] },
    desktop: { viewport: { width: 1600, height: 900 } },
};

const requested = process.argv[2] ?? "all";
const selected = requested === "all" ? Object.keys(profiles) : [requested];
for (const name of selected) {
    if (!profiles[name]) {
        console.error(`Unknown profile '${name}'.  Use: all, mobile, desktop.`);
        process.exit(1);
    }
}

const pages = [
    ["queues", "/queues"],
    ["events", "/events"],
    ["incidents", "/incidents"],
    ["decisions", "/decisions"],
    ["bans", "/bans"],
    ["audit", "/audit"],
    ["errors", "/errors"],
    ["signatures", "/signatures"],
    ["configuration", "/configuration"],
    ["account", "/account"],
];

function loadState() {
    return existsSync(STATE_PATH) ? JSON.parse(readFileSync(STATE_PATH, "utf8")) : {};
}

function saveState(state) {
    writeFileSync(STATE_PATH, JSON.stringify(state));
}

function totpCode(secret) {
    const result = generateSync({ secret });
    return typeof result === "string" ? result : (result.value ?? result.token);
}

/**
 * Logs in, completing the bootstrap password change and TOTP enrollment on
 * first contact with a fresh instance; later runs use the persisted secret.
 */
async function login(page, state) {
    await page.goto(`${BASE}/login`);
    await page.fill('input[name="username"]', USER);
    await page.fill('input[name="password"]', state.password ?? BOOTSTRAP_PASSWORD);
    await Promise.all([page.waitForLoadState("networkidle"), page.click('button[type="submit"]')]);

    if (page.url().includes("/login")) {
        // Bootstrap path: the 2FA page first demands a password change.
        if (await page.locator('input[name="newPassword"]').count() > 0) {
            await page.fill('input[name="newPassword"]', NEW_PASSWORD);
            await page.fill('input[name="confirmPassword"]', NEW_PASSWORD);
            await Promise.all([page.waitForLoadState("networkidle"), page.click('button[type="submit"]')]);
            state.password = NEW_PASSWORD;
        }

        // Either fresh TOTP enrollment (secret displayed) or a code prompt.
        if (await page.locator("pre.secret-block").count() > 0) {
            state.secret = (await page.locator("pre.secret-block").first().textContent()).replace(/\s+/g, "");
        }

        if (await page.locator('input[name="code"]').count() > 0) {
            await page.fill('input[name="code"]', totpCode(state.secret));
            await Promise.all([page.waitForLoadState("networkidle"), page.click('button[type="submit"]')]);
        }
    }

    if (page.url().includes("/login")) {
        throw new Error(`Login did not complete (still at ${page.url()}).  ` +
            "If the instance was restarted, delete shots/.state.json and retry.");
    }

    saveState(state);
}

const browser = await chromium.launch();
const state = loadState();

for (const profileName of selected) {
    const dir = `shots/${profileName}`;
    mkdirSync(dir, { recursive: true });
    const context = await browser.newContext(profiles[profileName]);
    const page = await context.newPage();

    await login(page, state);

    let index = 10;
    for (const [name, path] of pages) {
        await page.goto(`${BASE}${path}`);
        await page.waitForLoadState("networkidle");
        // networkidle can fire before first paint; wait for the rendered
        // header so screenshots never capture a blank frame.
        await page.waitForSelector(".admin-header", { state: "visible" });
        await page.screenshot({ path: `${dir}/${String(index).padStart(2, "0")}-${name}.png` });
        console.log(`${profileName}: ${name}`);
        index++;
    }

    // Profile-specific states worth a look every time.
    if (profileName === "mobile") {
        await page.goto(`${BASE}/bans`);
        await page.locator("label.nav-toggle-button").click();
        await page.screenshot({ path: `${dir}/30-menu-open.png` });
        console.log(`${profileName}: menu-open`);
    }

    await page.goto(`${BASE}/configuration#upgrades`);
    await page.waitForLoadState("networkidle");
    await page.waitForSelector(".admin-header", { state: "visible" });
    await page.screenshot({ path: `${dir}/31-configuration-upgrades.png` });
    await page.screenshot({ path: `${dir}/32-configuration-upgrades-full.png`, fullPage: true });
    console.log(`${profileName}: configuration-upgrades`);

    await context.close();
}

await browser.close();
console.log("done");
