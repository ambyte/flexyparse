# Host: running and configuration

Host provides the web UI, HTTP API, background jobs and conversion CLI. For the project logo, web-style wordmark, overview and quick start, see the [README](../README.md); for request examples, see the [API guide](api.md).

## Docker Compose

Requires Linux containers and Docker Compose 2.24+. Both stacks include FlexyParse and Gotenberg:

| Stack | Start command | Recognition |
| --- | --- | --- |
| [Standard build](../docker/docker-compose.image.yml) | docker compose --env-file .env -f docker/docker-compose.image.yml up --build -d | Configured provider; OpenAi by default. No GPU devices. |
| [GPU build](../docker/docker-compose.image.gpu.yml) | docker compose --env-file .env -f docker/docker-compose.image.gpu.yml up --build -d | NemotronGPU, Linux Intel/AMD Vulkan. |
| [Standard image](../docker/docker-compose.yml) | docker compose --env-file .env -f docker/docker-compose.yml up -d | Same standard configuration, published GHCR image. |
| [GPU image](../docker/docker-compose.gpu.yml) | docker compose --env-file .env -f docker/docker-compose.gpu.yml up -d | Same GPU configuration, published GHCR image. |

Run commands from the repository root after copying [the environment template](../.env.example) to [.env](../.env). The explicit environment-file argument supplies Compose interpolation; service settings also load that same root file. All stacks run standalone. Build stacks reuse the image stacks' service settings and add only build configuration. Stop one stack before starting another and use the same file selection for pull, logs, shutdown and other operations. All stacks explicitly use the project name **flexyparse**, preserving the existing default **flexyparse_layout-models** cache after the directory move. Deployments previously using a custom project name must continue supplying the same **-p** value.

- FlexyParse listens on container port 8080; FLEXYPARSE_PORT selects the host port (default 8080).
- Gotenberg is reachable at **http://gotenberg:3000** inside the stack and **http://localhost:3000** on the host. Its published port is loopback-only.
- FlexyParse starts after Gotenberg passes its health check. Both services use an automatic restart policy.
- Both variants use the same **layout-models** named volume mounted at the application model directory. It stores layout, RapidOCR and Nemotron models, not conversion results.
- Normal shutdown keeps the volume. Removing volumes forces model downloads again.
- Local build/probe artifacts and model caches are excluded from the Docker build context.

The public health route reports application liveness, not recognition readiness or Gotenberg availability. Nemotron loads inference graphs on the first recognition call; model downloads happen before startup completes.

## GitHub Actions and GHCR

The [container workflow](../.github/workflows/docker.yml) builds and publishes only for newly created tags matching **v*** whose commits belong to **main** history. Ordinary branch pushes and pull requests do not trigger it, and manual dispatch is disabled. A tag event cannot be filtered by branch directly in GitHub Actions: the initial read-only gate checks tag creation and commit ancestry using the full Git history. A tag outside main runs only this gate; validation and image jobs are skipped. Updating or deleting an existing tag does not run the build. A tag may point to the current main tip or an older commit already in main history.

After the gate passes, CI restores, builds with the existing warning-as-error/analyzer policy, runs .NET tests and Node deployment/UI regressions, then builds both [standard](../docker/Dockerfile) and [GPU](../docker/Dockerfile.gpu) images for **linux/amd64** using Buildx. GPU hardware inference and optional browser/model-download tests are not run in this workflow.

- No pull-request or privileged pull-request-target execution is configured.
- Accepted tag runs publish to **ghcr.io/ambyte/flexyparse** using the built-in GitHub token. The name is derived from the actual repository and lowercased, so forks publish to their own namespace.
- The validation job has read-only repository access. Only the image job receives package-write permission; no PAT or registry secret is stored in source.
- Each accepted tag updates **latest** and **latest-gpu**. A semantic version tag such as **v1.2.3** also publishes **1.2.3**, **1.2**, **1.2.3-gpu** and **1.2-gpu**. Commit tags are **sha-<short-commit>** and **sha-<short-commit>-gpu**.
- CPU/GPU builds use separate GitHub Actions caches and include OCI metadata, provenance and SBOM attestations.

Enable GitHub Actions and allow the workflow to write packages in repository/organization policies. For an existing GHCR package, grant this repository Actions access. After the first publication, make the package public if anonymous pulls are required; public repository visibility alone does not guarantee a public package. For a private package, authenticate Docker to GHCR with a token authorized to read that package; do not put that token in Compose files or the application environment file.

For published deployment, set **FLEXYPARSE_IMAGE** (default **ghcr.io/ambyte/flexyparse**) and **FLEXYPARSE_IMAGE_TAG** (default **latest**). Specify the base version only: the GPU stack adds **-gpu**. Run **pull** before **up -d** with the same image Compose file when updating; changing a remote tag does not update a running container automatically. These stacks have no build configuration and do not require the application sources, SDK or local model files. Keep the selected Compose file and its referenced image-stack files together under a docker directory, with the environment file in its parent directory.

Publishing begins only when this workflow is actually present and run on GitHub; creating it locally does not create an image in GHCR.

## GPU deployment

The [GPU image](../docker/Dockerfile.gpu) installs the Vulkan loader and Mesa drivers. This Compose recipe supports Linux Intel/AMD DRM devices; it is not a generic NVIDIA or Docker Desktop GPU configuration.

```sh
export FLEXYPARSE_GPU_RENDER_GID=$(stat -c '%g' /dev/dri/renderD128)
docker compose --env-file .env -f docker/docker-compose.image.gpu.yml up --build -d
```

Choose the render node belonging to your GPU. The required group ID grants the existing non-root application user access to mapped DRM devices. FLEXYPARSE_NEMOTRON_GPU_DEVICE_ID selects a zero-based device index in the WebGPU plugin's device list, not a CUDA index.

The GPU stack fixes the recognition provider to NemotronGPU, including the standard .NET provider key. It uses a separate FP16 cache; leave the Nemotron model-directory override empty when switching precision. Allow several GB of both RAM and VRAM. Missing drivers/devices fail explicitly; there is no automatic CPU-model fallback, and some graph operations can still run on CPU.

For native Host execution, the WebGPU plugin supports Windows/Linux x64 and ARM64 and macOS ARM64. A compatible driver/backend is required: D3D12/Vulkan on Windows, Vulkan on Linux, Metal on macOS. Linux additionally needs the Vulkan loader and a vendor-compatible ICD. CUDA/cuDNN are not used. Validate native inference on the target machine; a successful build alone does not verify GPU support.

## Local execution and CLI

Requires .NET SDK 10. Commands run from the repository root:

```sh
dotnet run --project src/FlexyParse.Host --no-launch-profile -- serve --urls http://localhost:8080
dotnet run --project src/FlexyParse.Host -- convert report.pdf --format markdown --output report.md --recognize-images false
dotnet run --project src/FlexyParse.Host -- convert a.pdf b.pdf --format html --output-dir results --recognize-images false
dotnet run --project src/FlexyParse.Host -- help
```

CLI formats are json (default), markdown and html. Output defaults to a file next to the source; use **--output -** for one result on stdout. Logs go to stderr. **--ocr-language eslav** overrides the RapidOCR profile for a conversion.

For local execution with Gotenberg published on host port 3000, use **http://localhost:3000** instead of the template's container Gotenberg address. Restart Host after changing configuration.

Deployment regression tests render Compose configuration in temporary directories without starting containers or reading developer settings. They verify both loading an existing root environment file and accepting its absence through resolved environment values. They do not assert that rendered output retains environment-file metadata, which differs across Compose versions. Personal startup scripts are not part of the test suite or required by CI.

## Configuration

Start with **MAIN SETTINGS** in the [environment template](../.env.example): port, incoming API key, recognition provider, model API and Gotenberg address. Separate sections cover local recognition, layout analysis, advanced API settings, processing limits, background jobs and debugging. Full defaults are in [Host settings](../src/FlexyParse.Host/appsettings.json).

Compose loads the local environment file. Direct .NET execution does not: set process environment variables or standard .NET configuration instead. Host priority, highest first:

1. Web startup configuration arguments.
2. Standard .NET environment keys, for example FlexyParse__Recognition__Provider.
3. Short FLEXYPARSE_* aliases.
4. Application JSON defaults.

Explicit empty values clear lower-priority values. The GPU Compose stack explicitly fixes the provider as noted above. Standalone Core does not implement Host's short aliases.

### Common settings

| Variable | Default | Purpose |
| --- | --- | --- |
| FLEXYPARSE_API_KEY | Empty | Incoming API authentication; unrelated to the model API key. |
| FLEXYPARSE_GOTENBERG_URL | Internal service URL in Compose | Office/HTML conversion; empty disables these formats. |
| FLEXYPARSE_GOTENBERG_TIMEOUT_SECONDS | 180 | Conversion deadline; also used by bundled Gotenberg. |
| FLEXYPARSE_MAX_UPLOAD_BYTES | 2147483648 | Per-document limit, including PDFs produced by Gotenberg. |
| FLEXYPARSE_MAX_FILES_PER_REQUEST | 16 | Batch size limit. |
| FLEXYPARSE_MAX_CONCURRENT_DOCUMENTS | 2 | Shared document processing concurrency. |
| FLEXYPARSE_MAX_CONCURRENT_IMAGE_RECOGNITIONS | 4 | OpenAI image-call concurrency. Local providers have their own gates. |
| FLEXYPARSE_RECOGNIZE_IMAGES | true | CLI/library default; HTTP still defaults to OCR enabled. |
| FLEXYPARSE_IMAGE_LAYOUT_ENABLED | true | Local PDF layout analysis and image segmentation; enabled in both the environment template and application defaults. |
| FLEXYPARSE_KEEP_RESULTS | false | Preserve sensitive workspaces/results for debugging; clean up manually. |

Use standard nested .NET keys for settings without a short alias. For example, FlexyParse__ImageInput__MaxPixels controls the decoded-image pixel limit.

### Supported conversion parameters

The HTTP API supports **to_formats**, **do_ocr**, **ocr_language**, **image_export_mode** and **include_images**. These options are executed by FlexyParse for uploaded files, URL/Base64 sources and background tasks. See the [API guide](api.md) for defaults and examples, **/v1/capabilities** for the supported option list and **/openapi.json** for validation schemas.

The application does not provide every Docling-specific OCR engine, model preset or enrichment stage. Unsupported fields are ignored and logged at Warning level under **FlexyParse.Hosting.ConversionParameters**, with their names in the structured **ParameterName** field. Values are not logged; control characters in names are escaped and names are bounded. JSON locations identify nested unsupported fields. Supported values still undergo normal validation, and size limits remain enforced. Synchronous and background requests use the same policy; background admission logs each unsupported field without repeating the warning in the worker. Requests are not forwarded to an external Docling service. Existing recognition-provider and Gotenberg settings continue to configure the application's own conversion pipeline.

### Small-image filter

Native recognition skips low-resolution icons/decorations before any provider or API-key requirement. These standard environment keys configure the inclusive thresholds:

| Key | Default |
| --- | --- |
| FlexyParse__ImageRecognitionFilter__Enabled | true |
| FlexyParse__ImageRecognitionFilter__MinShortSide | 24 |
| FlexyParse__ImageRecognitionFilter__MinLongSide | 80 |
| FlexyParse__ImageRecognitionFilter__MinPixels | 4096 |

All three size requirements must pass, regardless of rotation. A 32×32 icon, 64×64 thumbnail or 1000×8 separator is skipped; a 128×64 image, 400×24 text strip or full scanned page passes. This is not content classification: a large logo can still pass and a tiny useful label can be skipped. Adjust thresholds for your corpus; disable the filter when fidelity on tiny text matters more than recognition cost. Thresholds must be positive and MinLongSide must not be below MinShortSide.

The filter covers extracted PDF images, uploaded raster documents and padded layout crops, for every recognition provider. Skips are not failures. Originals remain available for image export. The same filtering applies to synchronous and background conversions.

### Recognition

Select the provider with FLEXYPARSE_RECOGNITION_PROVIDER:

| Provider | Main settings | Behavior |
| --- | --- | --- |
| OpenAi | FLEXYPARSE_MODEL_API_KEY, FLEXYPARSE_MODEL_API_BASE_URL, FLEXYPARSE_MODEL_API_MODEL | OpenAI-compatible vision Chat Completions. No local OCR downloads. |
| RapidOcr | FLEXYPARSE_RAPIDOCR_LANGUAGE (eslav) | Local CPU OCR, automatic pinned PP-OCRv5 models; plain text output. |
| NemotronCPU | FLEXYPARSE_NEMOTRON_INFERENCE_THREADS (4) | Local INT8 pack, about 761 MB. |
| NemotronGPU | Thread setting above and FLEXYPARSE_NEMOTRON_GPU_DEVICE_ID (0) | Local WebGPU FP16 pack, about 1.45 GB. |

For a local OpenAI-compatible server without authentication, set FLEXYPARSE_MODEL_API_REQUIRE_API_KEY=false. The model must accept images. Timeout, retries, token limit and image-size limits are in the environment template.

RapidOCR accepts model prefixes **arabic, ch, ch_server, cyrillic, devanagari, el, en, eslav, korean, latin, ta, te, th**. Use eslav for Russian/Ukrainian/Belarusian, latin for Western European languages, en for the dedicated English model, and ch/ch_server for Chinese mobile/server models. Language aliases and regional tags are not accepted. Request-level **ocr_language** selects a profile, not a different provider; it does not constrain OpenAI or Nemotron. See [preset definitions](../src/FlexyParse.Recognition.RapidOcr/RapidOcrModelPresets.cs) for exact model coverage.

Local models are downloaded automatically, SHA-256 verified and cached. Download progress and model loading are logged. A verified cache works offline. Nemotron's standard setting FlexyParse__Recognition__Nemotron__DownloadModels=false prohibits downloads. Local providers never silently fall back to an external API.

Nemotron emits Markdown and normalizes supported LaTeX tables into document blocks. Dense low-resolution inputs can produce transcription errors or generation loops. Repetition protection and bounded image recovery are enabled by default, but completion is not proof of accuracy. One shared Nemotron session serializes inference.

### Layout analysis

Local layout analysis is enabled by default, including startup without an environment file. First startup downloads the pinned YOLO DocLayNet model (about 61.5 MB); set FLEXYPARSE_IMAGE_LAYOUT_ENABLED=false or the standard .NET configuration value to disable it. Native PDF text remains native text: layout guides table extraction and reading order. Image regions are recognized by the selected provider.

Invalid/unsafe image layouts fall back to whole-image recognition or a full-coverage whitespace split. Without layout analysis, native table extraction is limited to ruled/lattice tables. Borderless table detection and reading order are heuristic; evaluate on representative documents. Startup provisioning errors are fatal rather than silently disabling requested features.

### Background jobs

| Variable | Default | Limit |
| --- | --- | --- |
| FLEXYPARSE_ASYNC_WORKER_COUNT | 2 | Concurrent background workers. |
| FLEXYPARSE_ASYNC_QUEUE_CAPACITY | 32 | Pending tasks, including admission/staging. |
| FLEXYPARSE_ASYNC_MAX_STORED_TASKS | 128 | All registered tasks, including retained completions. |
| FLEXYPARSE_ASYNC_RESULT_RETENTION_SECONDS | 3600 | Completed status/result retention. |
| FLEXYPARSE_ASYNC_CLEANUP_INTERVAL_SECONDS | 60 | Expiration cleanup interval. |
| FLEXYPARSE_ASYNC_MAX_WEBSOCKET_CONNECTIONS | 64 | Concurrent status observers. |

Values must be positive. Tasks/results belong to one Host process and do not survive restart. Multiple replicas need sticky routing; there is no distributed queue or durable task store.

## Deployment essentials

- Set a strong incoming API key and use TLS at a reverse proxy. All versioned API routes require the **X-Api-Key** header when enabled; UI, health and documentation stay public.
- Keep Gotenberg private. Outside Compose, use an address reachable from Host; container loopback is not the host machine.
- Bound uploads, concurrency, proxy deadlines and available temporary disk space. Large documents and local models can consume substantial RAM.
- Keep models writable during provisioning. With debug retention enabled, protect retained source documents and remove them manually; the model volume does not persist temporary workspaces.
- Test a representative document and a real GPU recognition call before production use.
