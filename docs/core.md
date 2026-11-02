# Core: architecture and .NET usage

FlexyParse targets .NET 10. Use Core from a .NET application without starting the HTTP service. The repository currently exposes project references, not a separately published NuGet package.

## Components

| Project | Responsibility |
| --- | --- |
| [Core](../src/FlexyParse.Core/FlexyParse.Core.csproj) | Conversion orchestration, PDF/image extraction, layout planning, Markdown parsing and JSON/Markdown/HTML output. Includes the OpenAI-compatible recognizer. |
| [RapidOCR](../src/FlexyParse.Recognition.RapidOcr/FlexyParse.Recognition.RapidOcr.csproj) | Local CPU OCR, language profiles and verified model provisioning. |
| [Nemotron](../src/FlexyParse.Recognition.Nemotron/FlexyParse.Recognition.Nemotron.csproj) | Local CPU INT8 / WebGPU FP16 inference and verified model provisioning. |
| [Host](../src/FlexyParse.Host/FlexyParse.Host.csproj) | UI, CLI, HTTP authentication, source downloads and process-local background tasks. |

Core defines the [recognition contract](../src/FlexyParse.Core/Recognition/IImageRecognizer.cs) and does not depend on the local provider projects. Host chooses and registers the configured provider; library consumers register local providers explicitly.

## Conversion pipeline

1. Stage the input in a bounded per-job workspace and acquire the shared document-processing gate.
2. Process Markdown directly; detect PDF and raster content from bytes. Other supported documents go through Gotenberg when configured.
3. Extract native PDF text/tables and embedded images, or create a single page for a raster image. Normalize orientation/transparency where needed; reject multi-frame images.
4. Optionally analyze layout and recognize image regions with the selected provider. Repeated PDF images share recognition results by content hash.
5. Write JSON, Markdown or HTML and clean up temporary job files.

Native PDF text is reflowed and ordered by columns. Optional YOLO layout analysis guides table candidates and image segmentation; without it, native tables use ruled/lattice extraction. Image-layout plans validate bounds, ordering and coverage. Unsafe plans use whole-image recognition or a full-coverage whitespace split. Layout and OCR are heuristics, not exact document reconstruction.

## Register and convert

Reference the [Core project](../src/FlexyParse.Core/FlexyParse.Core.csproj), then register [AddFlexyParse()](../src/FlexyParse.Core/DependencyInjection/FlexyParseServiceCollectionExtensions.cs:16) in a standard .NET host:

```csharp
using FlexyParse.DependencyInjection;
using FlexyParse.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddFlexyParse(builder.Configuration);
using var host = builder.Build();
await host.StartAsync();

try
{
    var converter = host.Services.GetRequiredService<IDocumentConverter>();
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
    await using var destination = File.Create("report.md");

    await converter.ConvertAsync(new ConversionInput
    {
        SourcePath = "report.pdf",
        FileName = "report.pdf",
        Format = OutputFormat.Markdown,
        RecognizeImages = false
    }, destination, timeout.Token);
}
finally
{
    await host.StopAsync();
}
```

The [conversion input](../src/FlexyParse.Core/Pipeline/ConversionInput.cs) accepts a local path or a readable content stream. A path takes precedence when both are provided. Supply a source filename; Markdown requires the Markdown extension. Recognition and RapidOCR profile can be overridden per call.

The caller owns input/output streams and must dispose them. Use independent streams for concurrent calls, never overwrite the source with the destination, and discard output after errors or cancellation. Source moving is opt-in and transfers the file into the disposable workspace; leave it disabled unless ownership transfer is intended.

## Local recognition modules

Reference the desired provider project and register it **after** Core, before building/starting the host:

- RapidOCR: [AddRapidOcrRecognition()](../src/FlexyParse.Recognition.RapidOcr/RapidOcrServiceCollectionExtensions.cs:13), with the configured provider set to RapidOcr.
- Nemotron: [AddNemotronRecognition()](../src/FlexyParse.Recognition.Nemotron/NemotronServiceCollectionExtensions.cs:16), with the configured provider set to NemotronCPU or NemotronGPU.

Registration replaces the shared recognizer and adds model-provisioning startup services. A configured local provider without its module fails explicitly; there is no external fallback. For layout analysis outside Host, provision the model through the [layout model store](../src/FlexyParse.Core/ImageLayout/ImageLayoutModelStore.cs) before conversion; Core registration alone does not install Host's layout startup service.

RapidOCR returns literal text. Nemotron generates Markdown and normalizes supported LaTeX tables into existing blocks. Nemotron execution precision is derived from the provider; do not configure a separate mode. It serializes inference and protects against repetition/context exhaustion. Bounded recovery may improve completion on difficult images, not transcription accuracy.

Models are pinned, SHA-256 verified and cached. Startup requires network access when assets are missing, plus a writable cache. A verified cache works offline. GPU/native library compatibility must be checked on the target platform. See [Host recognition settings](host.md#recognition) and [GPU deployment](host.md#gpu-deployment).

## Configuration and output

Core binds the **FlexyParse** configuration section through standard .NET configuration. For example, FlexyParse__ModelApi__ApiKey configures the model key. It does not load a local environment file or map Host's shortened aliases. The [Host environment template](../.env.example) groups primary setup separately from advanced tuning; it is a Host/deployment reference, not a Core configuration loader. See [option definitions](../src/FlexyParse.Core/Options) and [Host defaults](../src/FlexyParse.Host/appsettings.json).

Recognition defaults to enabled. Disable it per conversion when no recognizer is configured. Configure a reachable Gotenberg URL for Office/HTML input; PDF, Markdown and supported images do not require it.

JSON is the FlexyParse document schema, **not DoclingDocument**. It includes source filename, metadata and pages with typed paragraph, heading, table, image and error blocks. See [content models](../src/FlexyParse.Core/Model/ContentModels.cs). Library output is the requested document itself; Host adds its HTTP JSON wrapper or ZIP separately.

## Errors and resource ownership

- [DocumentConversionException](../src/FlexyParse.Core/Pipeline/DocumentConversionException.cs) carries a conversion status suitable for CLI/HTTP mapping.
- Cancellation propagates to staging, external calls and processing; do not publish partial stream output.
- Per-image recognition failures can be represented as document error blocks; inspect results when recognition completeness matters.
- Workspaces normally clean up on completion, failure and cancellation. Debug retention preserves source/recognition artifacts, which can contain sensitive content and need manual cleanup.
- Host's queue, task IDs, authentication and retention policy do not belong to Core. Synchronous and background HTTP requests reuse the same converter and processing gates.

The [README](../README.md) includes the project logo, web-style wordmark and overview; see [development instructions](../README.md#project-and-development) for build and regression-test commands.

## Container delivery and CI

Container CI accepts only newly created **v*** tags whose commits are contained in **main** history. An initial ancestry gate skips all build/test/publication jobs for tags outside main; branch pushes, pull requests and manual dispatch do not start the pipeline. This release policy does not change Core's APIs or runtime behavior.

The [GitHub Actions workflow](../.github/workflows/docker.yml) builds and tests the full solution before building standard/GPU Host containers and publishing them to GHCR. Core is included as a Host dependency; it is not a separate container service. Dockerfiles and local-build/ready-image Compose stacks live under [docker](../docker), with the repository root retained as the build context. The root [.dockerignore](../.dockerignore) excludes secrets, test outputs, local probes and model weights.

Compose naming distinguishes local builds from registry deployment: [docker-compose.image.yml](../docker/docker-compose.image.yml) and [docker-compose.image.gpu.yml](../docker/docker-compose.image.gpu.yml) build images locally; [docker-compose.yml](../docker/docker-compose.yml) and [docker-compose.gpu.yml](../docker/docker-compose.gpu.yml) use ready-made GHCR images without building.

Ready-image deployment uses the same Core pipeline and model provisioning as local builds. Model weights are downloaded and verified at runtime into the persistent cache, not embedded into images. Standard images do not expose GPU devices; the GPU variant adds Vulkan runtime dependencies and requires supported host hardware. No Core API or configuration-binding behavior changes are required for container publishing. See [Host CI and registry setup](host.md#github-actions-and-ghcr) for image tags, permissions and deployment commands.
