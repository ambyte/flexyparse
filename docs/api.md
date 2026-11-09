# HTTP API

Base URL: **<http://localhost:8080>**. Interactive documentation: **<http://localhost:8080/docs>**. Schema: **<http://localhost:8080/openapi.json>**.

FlexyParse applies only the Docling-style conversion parameters it can execute itself. Its JSON model is not DoclingDocument. The supported contract is documented below and exposed through **/openapi.json** and **/v1/capabilities**. Unsupported options are ignored with warning logs containing names only; requests are not delegated to an external Docling service.

## Authentication and routes

When an incoming Host API key is configured, send one **X-Api-Key** header on every versioned API request, including task status, results and WebSocket upgrades. Authentication is disabled when the configured key is empty. Health, web UI and API documentation remain public. See [Host configuration](host.md#configuration).

| Method | Route | Purpose |
| --- | --- | --- |
| POST | /v1/convert/file | Convert uploaded documents. |
| POST | /v1/convert/source | Convert public URL or Base64 sources. |
| POST | /v1/convert/file/async | Queue uploaded documents. |
| POST | /v1/convert/source/async | Queue URL/Base64 sources. |
| GET | /v1/status/poll/{task_id} | Read task status. |
| GET | /v1/result/{task_id} | Retrieve a completed result or conversion error. |
| WS | /v1/status/ws/{task_id} | Observe status changes. |
| GET | /v1/capabilities | Read supported formats, options and configured limits. |
| GET | /health | Application liveness, not dependency readiness. |

## Upload documents

Send multipart form data. Examples use curl; use curl.exe in Windows PowerShell if curl resolves to a shell alias.

```sh
curl -f http://localhost:8080/v1/convert/file -F "files=@report.pdf" -F "to_formats=md" -F "do_ocr=false" -o result.json
```

| Field | Values | Default |
| --- | --- | --- |
| files | Required file; repeat for a batch. | None |
| to_formats | md, json or html; repeat for multiple formats. | md |
| do_ocr | true or false; controls image recognition. | true |
| ocr_language | One RapidOCR model prefix. | Server profile, normally eslav |
| image_export_mode | placeholder, embedded or referenced. | placeholder |
| include_images | true or false; controls exported images, not recognition. | true |

HTTP recognition defaults to enabled **regardless of the CLI/library default**. Explicitly disable it when no recognizer is configured. Markdown conversion does not invoke OCR. Raster images go directly to recognition, without Gotenberg; multi-frame images are rejected.

Field names and output formats are case-sensitive. Unknown fields/options are ignored and logged at Warning level without their values. This includes unsupported multipart fields, JSON request properties, conversion options and source properties. Supported fields still require valid values: for example, an unknown table_mode option is ignored, but an invalid image_export_mode or do_ocr value returns 400. Do not send a JSON array in a multipart output-format field.

Ignored fields remain subject to request/section size limits and JSON syntax validation. Unknown multipart sections are drained without buffering their values. Logs use the **FlexyParse.Hosting.ConversionParameters** category and the structured **ParameterName** field. Names include the JSON location where applicable, such as **options.table_mode**; control characters are escaped and long names are truncated. Values, including nested model/API configurations, are never logged. Background requests log ignored parameters during admission, not again when the worker resolves sources.

Native image export is independent of do_ocr: **placeholder** emits image blocks without sources and Markdown/HTML image comments; **embedded** includes data URLs; **referenced** returns a ZIP with ordinary output files and an **images/** directory, even for one document. Links resolve relative to those output files after extracting the archive. **include_images=false** suppresses image export, including placeholders, but does not suppress recognized text. These same options are accepted inside the source request's **options** object and in asynchronous requests. Tiny images skipped by recognition can still be exported.

```sh
curl -f http://localhost:8080/v1/convert/file -F "files=@report.pdf" -F "to_formats=md" -F "do_ocr=false" -F "image_export_mode=referenced" -o result.zip
```

Recognition applies a configurable size filter by default: short side ≥24 px, long side ≥80 px and area ≥4096 pixels, including padded layout crops. Skips do not become errors. This is a heuristic rather than logo/text classification; see [filter settings](host.md#small-image-filter).

RapidOCR model prefixes: **arabic, ch, ch_server, cyrillic, devanagari, el, en, eslav, korean, latin, ta, te, th**. Prefixes are trimmed and case-insensitive; aliases such as ru and regional tags are invalid. The override selects a RapidOCR profile only, not the recognition provider. See [recognition settings](host.md#recognition).

For an authenticated request, add the incoming Host key:

```sh
curl -f http://localhost:8080/v1/capabilities -H "X-Api-Key: your-host-key"
```

## URL and Base64 sources

POST JSON to the source-conversion route. For example:

```json
{
  "http_sources": [{ "url": "https://example.org/report.pdf" }],
  "options": { "to_formats": ["md", "json"], "do_ocr": false }
}
```

Alternatively, replace the URL sources with Base64 files:

```json
{
  "file_sources": [{ "filename": "note.md", "base64_string": "IyBIZWxsbwo=" }],
  "options": { "to_formats": ["html"] }
}
```

Submit a saved payload:

```sh
curl -f http://localhost:8080/v1/convert/source -H "Content-Type: application/json" --data-binary @request.json -o result.json
```

URL and Base64 sources can be combined within the batch limit. Only public HTTP(S) URLs are accepted: no loopback/private destinations, credentials, fragments or redirects. Network access and decoded document size are bounded.

## Responses

**One document**, except when exporting referenced images, returns JSON containing the requested outputs:

```json
{
  "document": { "md_content": "# Report\n\nText" },
  "status": "success",
  "processing_time": 0.25,
  "timings": {},
  "errors": []
}
```

Depending on requested formats, the document includes **md_content**, **json_content** and/or **html_content**. JSON content is an object, not an encoded JSON string. The same wrapper is returned even when only Markdown or HTML is requested.

**Multiple documents** return a ZIP archive containing the requested output files. A conversion failure fails the batch; do not expect a partial-success archive.

Referenced image export with image inclusion enabled returns ZIP regardless of document count. The archive includes image assets and all requested output files; no response exposes links into a disposable server workspace.

## Background jobs

Use the asynchronous routes for long conversions:

```sh
curl -f http://localhost:8080/v1/convert/file/async -F "files=@report.pdf" -F "to_formats=md" -F "do_ocr=false"
```

The response supplies a **task_id**. Poll status, then retrieve the result:

```sh
curl -f http://localhost:8080/v1/status/poll/TASK_ID
curl -f http://localhost:8080/v1/result/TASK_ID -o result.json
```

Replace TASK_ID with the returned identifier. Status progresses through pending, started and success/failure. A WebSocket on the status route sends the current state and subsequent changes; closing an observer does not cancel processing.

Submission returns HTTP 200, not 202. Accepted work continues after the submitting client disconnects. Results use the same JSON/ZIP contract as synchronous conversion. Before completion, the result route returns 409; unknown or expired task IDs return 404. Failed tasks expose their conversion error through the result route.

Tasks and results are **process-local**, expire after the configured retention period (default one hour) and are lost on restart. There is no distributed queue; multiple replicas require sticky routing. See [queue limits](host.md#background-jobs).

## Errors and limits

Failures normally use **application/problem+json**:

| Status | Meaning |
| --- | --- |
| 400 | Invalid fields, unsupported format/content or invalid source. |
| 401 | Missing, invalid or duplicate incoming API key. |
| 404 | Task not found or expired. |
| 409 | Task result is not ready. |
| 413 | Input or converted document exceeds its limit. |
| 503 | Recognition/conversion service unavailable, or queue/status capacity exhausted. |
| 504 | Source download timed out. |

Default limits are **16 files** and **2 GiB per document**; decoded images and recognizers have additional limits. Query capabilities for configured upload limits. Consult the OpenAPI schema for full request and task-status shapes.
