const assert = require("node:assert/strict");
const { readFileSync } = require("node:fs");
const { resolve } = require("node:path");
const { test } = require("node:test");
const { runInNewContext } = require("node:vm");

const source = readFileSync(resolve(__dirname, "../src/FlexyParse.Host/wwwroot/app.js"), "utf8");

test("page header centers the brand horizontally", () => {
    const css = readFileSync(resolve(__dirname, "../src/FlexyParse.Host/wwwroot/app.css"), "utf8");
    assert.match(css, /header\s*\{[^}]*justify-content:\s*center\s*;/);
});

test("page versions both application assets to refresh cached scripts and styles together", () => {
    const html = readFileSync(resolve(__dirname, "../src/FlexyParse.Host/wwwroot/index.html"), "utf8");
    const cssVersion = /href="app\.css\?v=([^"]+)"/.exec(html)?.[1];
    const jsVersion = /src="app\.js\?v=([^"]+)"/.exec(html)?.[1];
    assert.ok(cssVersion);
    assert.equal(jsVersion, cssVersion);
    assert.match(html, /class="intro-description"/);
});

function setup() {
    const elements = new Map();
    const urls = { created: [], revoked: [] };
    const extensions = new Map();
    const intervals = new Map();
    const windowListeners = new Map();
    let now = 0;
    let intervalId = 0;
    function element(id) {
        if (!elements.has(id)) {
            const listeners = new Map();
            elements.set(id, {
                files: [], hidden: true, textContent: "", error: false,
                addEventListener(name, listener) { listeners.set(name, listener); },
                dispatch(name, detail = {}) {
                    const event = { detail, prevented: false, preventDefault() { this.prevented = true; } };
                    return { event, completion: listeners.get(name)?.(event) };
                },
                setAttribute(name, value) { this[name] = value; },
                removeAttribute(name) { delete this[name]; },
                focus() { this.focused = true; },
                classList: { toggle(name, value) { elements.get(id).error = value; } }
            });
        }
        return elements.get(id);
    }
    class BrowserUrl extends URL {
        static createObjectURL(blob) { urls.created.push(blob); return "blob:result"; }
        static revokeObjectURL(url) { urls.revoked.push(url); }
    }
    runInNewContext(source, {
        document: { getElementById: element },
        window: { addEventListener(name, listener) { windowListeners.set(name, listener); } },
        performance: { now: () => now },
        setInterval(callback) { intervals.set(++intervalId, callback); return intervalId; },
        clearInterval(id) { intervals.delete(id); },
        Blob,
        URL: BrowserUrl,
        htmx: { defineExtension(name, extension) { extensions.set(name, extension); } }
    });
    return {
        element, form: element("convert-form"), urls, extension: extensions.get("docling-source"),
        intervals,
        advance(milliseconds) { now += milliseconds; for (const callback of intervals.values()) { callback(); } },
        pagehide() { windowListeners.get("pagehide")(); }
    };
}

test("stopwatch tracks elapsed time for files and URLs, preserves completion time and resets on retry", async () => {
    for (const mode of ["files", "url"]) {
        const { form, element, advance, intervals } = setup();
        element("source-mode").value = mode;
        element("source-url").value = "https://example.com/report.pdf";
        const xhr = {
            status: 200, response: new Blob(["result"]),
            getResponseHeader() { return 'attachment; filename="result.zip"'; }
        };
        form.dispatch("htmx:beforeSend", { xhr });
        assert.equal(element("elapsed-time").textContent, "00:00:00");
        assert.equal(intervals.size, 1);
        advance(3_661_900);
        assert.equal(element("elapsed-time").textContent, "01:01:01");
        await form.dispatch("htmx:beforeOnLoad", { xhr }).completion;
        assert.equal(intervals.size, 0);
        advance(5000);
        assert.equal(element("elapsed-time").textContent, "01:01:01");
        form.dispatch("htmx:beforeSend", { xhr: {} });
        assert.equal(element("elapsed-time").textContent, "00:00:00");
        assert.equal(intervals.size, 1);
    }
});

test("stopwatch stops on cancellation, network failures and page exit", () => {
    for (const outcome of ["cancel", "htmx:sendError", "htmx:timeout", "htmx:sendAbort", "pagehide"]) {
        const { form, element, advance, intervals, pagehide } = setup();
        const xhr = { abort() {} };
        form.dispatch("htmx:beforeSend", { xhr });
        advance(2500);
        if (outcome === "cancel") {
            element("cancel-button").dispatch("click");
        } else if (outcome === "pagehide") {
            pagehide();
        } else {
            form.dispatch(outcome, { xhr });
        }
        assert.equal(intervals.size, 0);
        advance(5000);
        assert.equal(element("elapsed-time").textContent, "00:00:02");
    }
});

test("stopwatch stops on HTTP and invalid response errors", async () => {
    for (const status of [200, 500]) {
        const { form, element, advance, intervals } = setup();
        const xhr = {
            status, response: new Blob(["invalid JSON"]),
            getResponseHeader() { return "application/json"; }
        };
        form.dispatch("htmx:beforeSend", { xhr });
        advance(1200);
        await form.dispatch("htmx:beforeOnLoad", { xhr }).completion;
        assert.equal(element("status").error, true);
        assert.equal(intervals.size, 0);
        assert.equal(element("elapsed-time").textContent, "00:00:01");
    }
});

test("uses the Docling file endpoint for any number of files and shows filenames as text", () => {
    const { form, element } = setup();
    const files = element("files");
    files.files = [{ name: "<img src=x>.pdf" }];
    files.dispatch("change");
    assert.match(element("file-summary").textContent, /<img src=x>/);
    assert.equal(form.dispatch("htmx:configRequest").event.detail.path, "v1/convert/file");
    files.files.push({ name: "second.md" });
    assert.equal(form.dispatch("htmx:configRequest").event.detail.path, "v1/convert/file");
});

test("sends an optional API key as a header for files and URLs, never as source data", () => {
    const { form, element, extension } = setup();
    for (const mode of ["files", "url"]) {
        element("source-mode").value = mode;
        element("source-url").value = "https://example.com/report.md";
        element("api-key").value = "test-host-key";
        const configured = form.dispatch("htmx:configRequest", { headers: {} }).event.detail;
        assert.equal(configured.headers["X-Api-Key"], "test-host-key");
        assert.doesNotMatch(extension.encodeParameters() || "", /test-host-key/);
        element("api-key").value = "";
        const anonymous = form.dispatch("htmx:configRequest", { headers: {} }).event.detail;
        assert.equal(anonymous.headers["X-Api-Key"], undefined);
    }
});

test("extracts requested content from a Docling envelope without rendering HTML", async () => {
    for (const [format, field, content] of [
        ["md", "md_content", "# Report"],
        ["html", "html_content", "<script>not executed</script>"],
        ["json", "json_content", { pages: [] }]
    ]) {
        const { form, element, urls } = setup();
        element("format").value = format;
        element("files").files = [{ name: "Test.pdf" }];
        const xhr = {
            status: 200,
            response: new Blob([JSON.stringify({ status: "success", document: { [field]: content } })]),
            getResponseHeader(name) { return name === "Content-Type" ? "application/json" : null; }
        };
        form.dispatch("htmx:beforeSend", { xhr });
        // A later selection must not change how the active result is interpreted.
        element("format").value = "other";
        await form.dispatch("htmx:beforeOnLoad", { xhr }).completion;
        assert.equal(element("download").download, `Test.${format}`);
        assert.equal(await urls.created[0].text(), typeof content === "string" ? content : JSON.stringify(content, null, 2));
        assert.equal(element("status").error, false);
    }
});

test("switches required inputs between file and URL modes without losing selected files", () => {
    const { element } = setup();
    const files = element("files");
    const url = element("source-url");
    const mode = element("source-mode");
    files.files = [{ name: "report.md" }];
    assert.equal(files.required, true);
    assert.equal(url.disabled, true);
    mode.value = "url";
    mode.dispatch("change");
    assert.equal(files.disabled, true);
    assert.equal(files.required, false);
    assert.equal(url.required, true);
    assert.equal(url.disabled, false);
    assert.equal(element("file-source").hidden, true);
    assert.equal(element("url-source").hidden, false);
    mode.value = "files";
    mode.dispatch("change");
    assert.equal(files.disabled, false);
    assert.equal(files.required, true);
    assert.equal(url.required, false);
    assert.equal(url.disabled, true);
    assert.equal(files.files[0].name, "report.md");
});

test("encodes URL sources as Docling JSON and keeps local files as multipart", () => {
    const { form, element, extension } = setup();
    assert.equal(extension.encodeParameters(), null);
    element("source-mode").value = "url";
    element("source-mode").dispatch("change");
    element("source-url").value = " https://example.com/report.pdf?token=secret ";
    element("format").value = "md";
    element("recognize-images").value = "false";
    const configuration = form.dispatch("htmx:configRequest", { headers: {} }).event.detail;
    assert.equal(configuration.path, "v1/convert/source");
    assert.equal(configuration.headers["Content-Type"], "application/json");
    assert.deepEqual(JSON.parse(extension.encodeParameters()), {
        http_sources: [{ url: "https://example.com/report.pdf?token=secret" }],
        options: { to_formats: ["md"], do_ocr: false }
    });
    element("recognize-images").value = "true";
    assert.equal(JSON.parse(extension.encodeParameters()).options.do_ocr, true);
    element("source-mode").value = "files";
    element("source-mode").dispatch("change");
    assert.equal(extension.encodeParameters(), null);
    const fileConfig = form.dispatch("htmx:configRequest", { headers: {} }).event.detail;
    assert.equal(fileConfig.path, "v1/convert/file");
    assert.equal(fileConfig.headers["Content-Type"], undefined);
});

test("downloads URL result with a decoded filename and does not show fake download progress", async () => {
    for (const [url, name] of [
        ["https://example.com/%D0%A2%D0%B5%D1%81%D1%82.pdf?token=secret", "Тест.md"],
        ["https://example.com/", "document.md"]
    ]) {
        const { form, element, urls } = setup();
        element("source-mode").value = "url";
        element("source-url").value = url;
        element("format").value = "md";
        element("files").files = [{ name: "ignored.pdf" }];
        const xhr = {
            status: 200,
            response: new Blob([JSON.stringify({ status: "success", document: { md_content: "# Report" } })]),
            getResponseHeader(header) { return header === "Content-Type" ? "application/json" : null; }
        };
        form.dispatch("htmx:beforeSend", { xhr });
        assert.equal(element("upload-progress").hidden, true);
        assert.match(element("status").textContent, /downloading the document by URL/);
        form.dispatch("htmx:xhr:progress", { lengthComputable: true, loaded: 100, total: 100 });
        assert.equal(element("upload-progress").value, 0);
        element("source-mode").value = "files";
        element("source-mode").dispatch("change");
        element("source-url").value = "https://example.com/other.pdf";
        await form.dispatch("htmx:beforeOnLoad", { xhr }).completion;
        assert.equal(element("download").download, name);
        assert.equal(await urls.created[0].text(), "# Report");
    }
});

test("cancels a URL request using the same request lifecycle", () => {
    const { form, element } = setup();
    element("source-mode").value = "url";
    element("source-url").value = "https://example.com/report.pdf";
    let aborted = false;
    const xhr = { abort() { aborted = true; form.dispatch("htmx:sendAbort", { xhr: this }); } };
    form.dispatch("htmx:beforeSend", { xhr });
    element("cancel-button").dispatch("click");
    assert.equal(aborted, true);
    assert.equal(element("busy").hidden, true);
    assert.equal(form["aria-busy"], "false");
    assert.equal(element("status").error, false);
});

test("rejects missing or failed document envelopes", async () => {
    for (const payload of [{ status: "failure", document: {} }, { status: "success" }]) {
        const { form, element, urls } = setup();
        element("format").value = "md";
        const xhr = {
            status: 200, response: new Blob([JSON.stringify(payload)]),
            getResponseHeader() { return "application/json"; }
        };
        form.dispatch("htmx:beforeSend", { xhr });
        await form.dispatch("htmx:beforeOnLoad", { xhr }).completion;
        assert.equal(element("status").error, true);
        assert.equal(urls.created.length, 0);
    }
});

test("preserves binary response, uses UTF-8 download name and revokes previous URL", async () => {
    const { form, element, urls } = setup();
    const xhr = {
        status: 200,
        response: new Blob(["<script>not executed</script>"]),
        getResponseHeader() { return "attachment; filename*=UTF-8''%D0%A2%D0%B5%D1%81%D1%82.html"; }
    };
    form.dispatch("htmx:beforeSend", { xhr });
    assert.equal(xhr.responseType, "blob");
    assert.equal(form["aria-busy"], "true");
    const response = form.dispatch("htmx:beforeOnLoad", { xhr });
    await response.completion;
    assert.equal(response.event.prevented, true);
    assert.equal(urls.created[0], xhr.response);
    assert.equal(element("download").download, "Тест.html");
    assert.equal(element("download").href, "blob:result");
    assert.equal(element("download").hidden, false);
    assert.equal(form["aria-busy"], "false");
    form.dispatch("htmx:beforeSend", { xhr });
    assert.deepEqual(urls.revoked, ["blob:result"]);
    assert.equal(element("download").hidden, true);
});

test("displays API problem details without creating a download", async () => {
    const { form, element, urls } = setup();
    const xhr = {
        status: 413,
        response: new Blob([JSON.stringify({ detail: "File too large" })]),
        getResponseHeader() { return "application/problem+json"; }
    };
    form.dispatch("htmx:beforeSend", { xhr });
    await form.dispatch("htmx:beforeOnLoad", { xhr }).completion;
    assert.equal(element("status").textContent, "File too large");
    assert.equal(element("status").error, true);
    assert.equal(urls.created.length, 0);
    assert.equal(element("busy").hidden, true);
});

test("handles non-JSON errors, malformed JSON and network failures", async () => {
    for (const contentType of ["text/html", "application/json"]) {
        const { form, element } = setup();
        const xhr = {
            status: 500, response: new Blob(["<html>failure</html>"]),
            getResponseHeader() { return contentType; }
        };
        form.dispatch("htmx:beforeSend", { xhr });
        await form.dispatch("htmx:beforeOnLoad", { xhr }).completion;
        assert.equal(element("status").error, true);
        assert.equal(element("download").hidden, true);
    }
    for (const eventName of ["htmx:sendError", "htmx:timeout", "htmx:sendAbort"]) {
        const { form, element } = setup();
        form.dispatch(eventName);
        assert.equal(element("status").error, true);
        assert.equal(element("busy").hidden, true);
        assert.equal(form["aria-busy"], "false");
    }
});

test("updates upload progress", () => {
    const { form, element } = setup();
    form.dispatch("htmx:xhr:progress", { lengthComputable: true, loaded: 25, total: 100 });
    assert.equal(element("upload-progress").value, 25);
});

test("cancels the active request without showing a network error and allows retry", async () => {
    const { form, element, urls } = setup();
    let aborts = 0;
    const xhr = {
        abort() {
            aborts++;
            form.dispatch("htmx:sendAbort", { xhr: this });
        }
    };
    const cancel = element("cancel-button");
    form.dispatch("htmx:beforeSend", { xhr });
    assert.equal(cancel.hidden, false);
    cancel.dispatch("click");
    assert.equal(aborts, 1);
    assert.equal(cancel.hidden, true);
    assert.equal(element("busy").hidden, true);
    assert.equal(element("upload-progress").hidden, true);
    assert.equal(form["aria-busy"], "false");
    assert.equal(element("status").error, false);
    assert.match(element("status").textContent, /process has been cancelled/);
    assert.equal(element("convert-button").focused, true);
    assert.equal(element("download").hidden, true);
    cancel.dispatch("click");
    assert.equal(aborts, 1);

    const next = {
        status: 200, response: new Blob(["result"]),
        getResponseHeader() { return 'attachment; filename="result.json"'; }
    };
    form.dispatch("htmx:beforeSend", { xhr: next });
    // Late events from the cancelled request must not finish the new request.
    form.dispatch("htmx:sendAbort", { xhr });
    form.dispatch("htmx:sendError", { xhr });
    await form.dispatch("htmx:beforeOnLoad", { xhr }).completion;
    assert.equal(form["aria-busy"], "true");
    assert.equal(cancel.hidden, false);
    assert.equal(urls.created.length, 0);
    await form.dispatch("htmx:beforeOnLoad", { xhr: next }).completion;
    assert.equal(element("download").hidden, false);
    assert.equal(cancel.hidden, true);
    assert.equal(form["aria-busy"], "false");
});

test("ignores an error body that finishes reading after cancellation and retry", async () => {
    const { form, element } = setup();
    let completeBody;
    const xhr = {
        status: 500,
        response: { text() { return new Promise(resolve => { completeBody = resolve; }); } },
        getResponseHeader() { return "application/problem+json"; },
        abort() {}
    };
    form.dispatch("htmx:beforeSend", { xhr });
    const pending = form.dispatch("htmx:beforeOnLoad", { xhr }).completion;
    element("cancel-button").dispatch("click");
    form.dispatch("htmx:beforeSend", { xhr: {} });
    completeBody('{"detail":"Old error"}');
    await pending;
    assert.equal(form["aria-busy"], "true");
    assert.equal(element("cancel-button").hidden, false);
    assert.equal(element("status").error, false);
    assert.doesNotMatch(element("status").textContent, /Old error/);
});
