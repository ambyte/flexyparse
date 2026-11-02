const assert = require("node:assert/strict");
const { readFileSync } = require("node:fs");
const { createServer } = require("node:http");
const { resolve } = require("node:path");
const { test } = require("node:test");

// Optional real-browser coverage; regular UI tests need no external packages.
let chromium;
try {
    ({ chromium } = require("playwright"));
} catch (error) {
    if (error.code !== "MODULE_NOT_FOUND") { throw error; }
}

test("real htmx requests run and stop the stopwatch; description fills desktop and mobile layouts", {
    skip: !chromium && "Install Playwright and make it available through NODE_PATH to run browser coverage."
}, async t => {
    const root = resolve(__dirname, "../src/FlexyParse.Host/wwwroot");
    const assets = new Map([
        ["/", ["index.html", "text/html"]],
        ["/app.css", ["app.css", "text/css"]],
        ["/app.js", ["app.js", "text/javascript"]],
        ["/lib/htmx/htmx.min.js", ["lib/htmx/htmx.min.js", "text/javascript"]]
    ]);
    const server = createServer((request, response) => {
        const asset = assets.get(new URL(request.url, "http://localhost").pathname);
        if (!asset) { response.writeHead(404).end(); return; }
        response.writeHead(200, { "Content-Type": asset[1] });
        response.end(readFileSync(resolve(root, asset[0])));
    });
    await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
    t.after(() => new Promise(resolve => server.close(resolve)));
    const browser = await chromium.launch({ channel: process.env.PLAYWRIGHT_CHANNEL || "msedge", headless: true });
    t.after(() => browser.close());
    const page = await browser.newPage();
    const errors = [];
    page.on("pageerror", error => errors.push(error.message));
    let releaseResponse;
    await page.route("**/v1/convert/*", async route => {
        await new Promise(resolve => { releaseResponse = resolve; });
        await route.fulfill({
            status: 200, contentType: "application/json",
            body: JSON.stringify({ status: "success", document: { json_content: { pages: [] } } })
        });
    });
    await page.goto(`http://127.0.0.1:${server.address().port}/`);
    for (const width of [1280, 390]) {
        await page.setViewportSize({ width, height: 900 });
        const layout = await page.locator(".intro-description").evaluate(element => ({
            width: element.getBoundingClientRect().width,
            parentWidth: element.parentElement.getBoundingClientRect().width,
            alignment: getComputedStyle(element).textAlign,
            overflows: document.documentElement.scrollWidth > window.innerWidth
        }));
        assert.equal(layout.width, layout.parentWidth);
        assert.equal(layout.alignment, "justify");
        assert.equal(layout.overflows, false);
    }
    for (const mode of ["files", "url"]) {
        await page.selectOption("#source-mode", mode);
        if (mode === "files") {
            await page.setInputFiles("#files", { name: "test.md", mimeType: "text/markdown", buffer: Buffer.from("# Test") });
        } else {
            await page.fill("#source-url", "https://example.com/test.md");
        }
        releaseResponse = undefined;
        await page.click("#convert-button");
        await page.waitForFunction(() => document.querySelector("#elapsed-time").textContent !== "00:00:00");
        assert.equal(await page.locator("#convert-form").getAttribute("aria-busy"), "true");
        assert.ok(releaseResponse);
        releaseResponse();
        await page.waitForFunction(() => document.querySelector("#convert-form").getAttribute("aria-busy") === "false");
        const finalTime = await page.locator("#elapsed-time").textContent();
        await page.waitForTimeout(1100);
        assert.equal(await page.locator("#elapsed-time").textContent(), finalTime);
        assert.equal(await page.locator("#download").isVisible(), true);
    }
    assert.deepEqual(errors, []);
});
