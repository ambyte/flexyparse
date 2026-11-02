(() => {
    "use strict";

    const form = document.getElementById("convert-form");
    const files = document.getElementById("files");
    const summary = document.getElementById("file-summary");
    const status = document.getElementById("status");
    const download = document.getElementById("download");
    const progress = document.getElementById("upload-progress");
    const busy = document.getElementById("busy");
    const cancel = document.getElementById("cancel-button");
    const convert = document.getElementById("convert-button");
    const format = document.getElementById("format");
    const sourceMode = document.getElementById("source-mode");
    const sourceUrl = document.getElementById("source-url");
    const fileSource = document.getElementById("file-source");
    const urlSource = document.getElementById("url-source");
    const recognition = document.getElementById("recognize-images");
    const apiKey = document.getElementById("api-key");
    const elapsedTime = document.getElementById("elapsed-time");
    const requestOutputs = new WeakMap();
    const cancelledRequests = new WeakSet();
    let activeRequest;
    let downloadUrl;
    let timerStartedAt;
    let timerInterval;

    function updateTimer() {
        const seconds = Math.floor((performance.now() - timerStartedAt) / 1000);
        elapsedTime.textContent = [Math.floor(seconds / 3600), Math.floor(seconds / 60) % 60, seconds % 60]
            .map(value => String(value).padStart(2, "0")).join(":");
    }

    function stopTimer() {
        if (timerInterval !== undefined) {
            clearInterval(timerInterval);
            timerInterval = undefined;
            updateTimer();
        }
    }

    function startTimer() {
        stopTimer();
        timerStartedAt = performance.now();
        updateTimer();
        timerInterval = setInterval(updateTimer, 250);
    }

    function isUrlSource() {
        return sourceMode.value === "url";
    }

    function updateSourceMode() {
        const isUrl = isUrlSource();
        fileSource.hidden = isUrl;
        urlSource.hidden = !isUrl;
        files.disabled = isUrl;
        files.required = !isUrl;
        sourceUrl.disabled = !isUrl;
        sourceUrl.required = isUrl;
    }

    function sourceFilename() {
        if (!isUrlSource()) {
            return files.files[0]?.name || "document";
        }
        try {
            const path = new URL(sourceUrl.value.trim()).pathname;
            return decodeURIComponent(path.split("/").pop()).split(/[\\/]/).pop() || "document.pdf";
        } catch {
            return "document.pdf";
        }
    }

    // Keep htmx's request lifecycle, cancellation and multipart encoding for local files;
    // only URL sources need the existing API's JSON request body.
    htmx.defineExtension("docling-source", {
        encodeParameters() {
            if (!isUrlSource()) {
                return null;
            }
            return JSON.stringify({
                http_sources: [{ url: sourceUrl.value.trim() }],
                options: { to_formats: [format.value], do_ocr: recognition.value === "true" }
            });
        }
    });
    sourceMode.addEventListener("change", updateSourceMode);
    updateSourceMode();

    function clearDownload() {
        if (downloadUrl) {
            URL.revokeObjectURL(downloadUrl);
            downloadUrl = undefined;
        }
        download.hidden = true;
        download.removeAttribute("href");
    }

    function showStatus(message, isError = false) {
        status.textContent = message;
        status.classList.toggle("error", isError);
    }

    function finish() {
        stopTimer();
        activeRequest = undefined;
        form.setAttribute("aria-busy", "false");
        busy.hidden = true;
        progress.hidden = true;
        cancel.hidden = true;
    }

    cancel.addEventListener("click", () => {
        if (!activeRequest) {
            return;
        }

        const xhr = activeRequest;
        cancelledRequests.add(xhr);
        // Aborting the actual request also lets htmx release its request lock
        // and re-enable the submit button. The server observes RequestAborted.
        xhr.abort();
        finish();
        showStatus("The process has been cancelled. You can start the conversion again.");
        convert.focus();
    });

    files.addEventListener("change", () => {
        const selected = Array.from(files.files);
        summary.textContent = selected.length
            ? `${selected.length} file(s): ${selected.map(file => file.name).join(", ")}`
            : "No files have been selected yet.";
    });

    form.addEventListener("htmx:configRequest", event => {
        event.detail.path = isUrlSource() ? "v1/convert/source" : "v1/convert/file";
        if (apiKey.value) {
            event.detail.headers["X-Api-Key"] = apiKey.value;
        }
        if (isUrlSource()) {
            event.detail.headers["Content-Type"] = "application/json";
        }
    });

    form.addEventListener("htmx:beforeSend", event => {
        // JSON envelopes and ZIP attachments must never be treated as HTML fragments.
        event.detail.xhr.responseType = "blob";
        requestOutputs.set(event.detail.xhr, {
            format: format.value,
            filename: sourceFilename(),
            isUrl: isUrlSource()
        });
        activeRequest = event.detail.xhr;
        startTimer();
        clearDownload();
        form.setAttribute("aria-busy", "true");
        busy.hidden = false;
        cancel.hidden = false;
        progress.value = 0;
        progress.hidden = isUrlSource();
        busy.textContent = isUrlSource() ? "Downloading and processing document…" : "Processing document…";
        showStatus(isUrlSource()
            ? "The server is downloading the document by URL and executing the conversion… This may take some time."
            : "Uploading files and converting… This may take some time.");
    });

    form.addEventListener("htmx:xhr:progress", event => {
        if (activeRequest && requestOutputs.get(activeRequest)?.isUrl) {
            return;
        }
        if (event.detail.lengthComputable) {
            progress.value = event.detail.loaded / event.detail.total * 100;
        }
    });

    form.addEventListener("htmx:beforeOnLoad", async event => {
        // Cancel before htmx attempts to interpret a binary or untrusted HTML result.
        event.preventDefault();
        const xhr = event.detail.xhr;
        if (xhr !== activeRequest || cancelledRequests.has(xhr)) {
            return;
        }
        try {
            if (xhr.status < 200 || xhr.status >= 300) {
                let message = `Failed to execute conversion (HTTP ${xhr.status}).`;
                if (xhr.getResponseHeader("Content-Type")?.includes("json")) {
                    const problem = JSON.parse(await xhr.response.text());
                    if (xhr !== activeRequest) {
                        return;
                    }
                    message = problem.detail || problem.title || message;
                }
                showStatus(message, true);
                return;
            }

            let blob = xhr.response;
            const disposition = xhr.getResponseHeader("Content-Disposition") || "";
            const encodedName = /filename\*=UTF-8''([^;]+)/i.exec(disposition);
            const plainName = /filename="([^"]+)"|filename=([^;]+)/i.exec(disposition);
            let name = "flexyparse.zip";
            if (encodedName) {
                name = decodeURIComponent(encodedName[1]);
            } else if (plainName) {
                name = (plainName[1] || plainName[2]).trim();
            }

            if (xhr.getResponseHeader("Content-Type")?.includes("application/json")) {
                const result = JSON.parse(await xhr.response.text());
                if (xhr !== activeRequest) {
                    return;
                }
                const output = requestOutputs.get(xhr);
                const field = { md: "md_content", json: "json_content", html: "html_content" }[output.format];
                if (result.status !== "success" || !result.document || !(field in result.document)) {
                    throw new Error("Missing converted document");
                }
                const content = output.format === "json"
                    ? JSON.stringify(result.document[field], null, 2)
                    : result.document[field];
                const type = { md: "text/markdown", json: "application/json", html: "text/html" }[output.format];
                blob = new Blob([content], { type: `${type};charset=utf-8` });
                name = output.filename.replace(/\.[^.]+$/, "") + "." + output.format;
            }

            downloadUrl = URL.createObjectURL(blob);
            download.href = downloadUrl;
            download.download = name;
            download.textContent = `Download ${name}`;
            download.hidden = false;
            showStatus("Ready! Download the result using the link below.");
            download.focus();
        } catch {
            if (xhr === activeRequest) {
                showStatus("Failed to read server response. Please try again.", true);
            }
        } finally {
            if (xhr === activeRequest) {
                finish();
            }
        }
    });

    for (const eventName of ["htmx:sendError", "htmx:timeout", "htmx:sendAbort"]) {
        form.addEventListener(eventName, event => {
            const xhr = event.detail.xhr;
            if (xhr && (xhr !== activeRequest || cancelledRequests.has(xhr))) {
                return;
            }
            finish();
            showStatus("The request was cancelled or the server is unavailable. Please check your connection and try again.", true);
        });
    }

    window.addEventListener("pagehide", () => {
        stopTimer();
        clearDownload();
    });
})();
