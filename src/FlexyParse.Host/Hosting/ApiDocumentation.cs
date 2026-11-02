using System.Text.Json.Nodes;
using FlexyParse.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace FlexyParse.Hosting;

public static class ApiDocumentation
{
    public static void AddServices(IServiceCollection services)
    {
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("openapi", new OpenApiInfo
            {
                Title = "FlexyParse HTTP API",
                Version = "v1",
                Description = "Supported synchronous and asynchronous subset of Docling REST API. "
                    + "The JSON document uses the FlexyParse model, not DoclingDocument. "
                    + "When a host API key is configured, /v1 routes require X-Api-Key. Health, web UI and documentation are public."
            });
            options.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Header,
                Name = ApiKeyAuthentication.HeaderName,
                Description = "Host API key. Required only when FlexyParse:Host:ApiKey is configured."
            });
            options.OperationFilter<ConversionOperationFilter>();
        });
    }

    public static void Use(WebApplication app)
    {
        // The document name is 'openapi', producing /openapi.json without an alias route.
        app.UseSwagger(options => options.RouteTemplate = "{documentName}.json");
        app.UseSwaggerUI(options =>
        {
            options.RoutePrefix = "docs";
            options.SwaggerEndpoint("../openapi.json", "FlexyParse v1");
            options.DocumentTitle = "FlexyParse API — Swagger UI";
            options.EnableTryItOutByDefault();
            options.ConfigObject.ValidatorUrl = null;
        });
    }
}

// The handlers deliberately read HttpContext streams, so ApiExplorer cannot infer their bodies.
// Describe the wire contract without changing streaming uploads to buffered IFormFile binding.
internal sealed class ConversionOperationFilter(IOptions<FlexyParseOptions> settings, IOptions<ApiKeyOptions> authentication) : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var path = context.ApiDescription.RelativePath?.Replace(":guid}", "}", StringComparison.Ordinal);
        switch (path)
        {
            case "v1/convert/file":
            case "v1/convert/file/async":
                operation.OperationId = "convertFile";
                operation.Summary = "Convert uploaded documents";
                operation.RequestBody = new OpenApiRequestBody
                {
                    Required = true,
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["multipart/form-data"] = new()
                        {
                            Schema = Object(new()
                            {
                                ["files"] = new OpenApiSchema
                                {
                                    Type = JsonSchemaType.Array,
                                    Items = Binary(),
                                    MinItems = 1,
                                    MaxItems = settings.Value.MaxFilesPerRequest,
                                    Description = $"Repeat this field for multiple files. Maximum {settings.Value.MaxUploadBytes} bytes per document."
                                },
                                ["to_formats"] = Formats(),
                                ["do_ocr"] = Ocr(),
                                ["ocr_language"] = OcrLanguage()
                            }, "files"),
                            Encoding = new Dictionary<string, OpenApiEncoding>
                            {
                                ["to_formats"] = new() { Style = ParameterStyle.Form, Explode = true }
                            }
                        }
                    }
                };
                ConversionResponses(operation, context);
                break;
            case "v1/convert/source":
            case "v1/convert/source/async":
                operation.OperationId = "convertSource";
                operation.Summary = "Convert public HTTP(S) URLs or Base64 files";
                operation.RequestBody = new OpenApiRequestBody
                {
                    Required = true,
                    Description = "Provide at least one source. The combined source count must not exceed "
                        + settings.Value.MaxFilesPerRequest + ". Null values and unknown properties are rejected. "
                        + "URLs must be public HTTP(S) addresses without credentials or fragments. Redirects are not followed.",
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["application/json"] = new()
                        {
                            Schema = Object(new()
                            {
                                ["http_sources"] = new OpenApiSchema
                                {
                                    Type = JsonSchemaType.Array,
                                    MaxItems = settings.Value.MaxFilesPerRequest,
                                    Items = Object(new()
                                    {
                                        ["url"] = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uri" }
                                    }, "url")
                                },
                                ["file_sources"] = new OpenApiSchema
                                {
                                    Type = JsonSchemaType.Array,
                                    MaxItems = settings.Value.MaxFilesPerRequest,
                                    Items = Object(new()
                                    {
                                        ["filename"] = new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 1 },
                                        ["base64_string"] = new OpenApiSchema
                                        {
                                            Type = JsonSchemaType.String,
                                            Format = "byte",
                                            MinLength = 1,
                                            Description = $"Standard Base64, without a data URL prefix. Maximum {settings.Value.MaxUploadBytes} decoded bytes."
                                        }
                                    }, "filename", "base64_string")
                                },
                                ["options"] = Object(new() { ["to_formats"] = Formats(), ["do_ocr"] = Ocr(), ["ocr_language"] = OcrLanguage() })
                            }),
                            Example = JsonNode.Parse("""
                                {
                                  "file_sources": [{ "filename": "hello.md", "base64_string": "IyBIZWxsbw==" }],
                                  "options": { "to_formats": ["md"], "do_ocr": false }
                                }
                                """)
                        }
                    }
                };
                ConversionResponses(operation, context);
                operation.Responses!["504"] = Problem("HTTP source download timed out.", context);
                break;
            case "health":
                operation.OperationId = "health";
                operation.Summary = "Check process liveness (not external dependencies)";
                operation.Responses = new OpenApiResponses
                {
                    ["200"] = JsonResponse(Object(new()
                    {
                        ["status"] = new OpenApiSchema { Type = JsonSchemaType.String, Enum = [JsonValue.Create("ok")!] }
                    }, "status"), "The process is running.")
                };
                break;
            case "v1/capabilities":
                operation.OperationId = "capabilities";
                operation.Summary = "List supported formats, options, sources and configured limits";
                operation.Description = "FlexyParse capabilities, not the full Docling Serve capabilities schema. "
                    + "Additional input formats depend on Gotenberg when configured. Background tasks are process-local and expire.";
                operation.Responses = new OpenApiResponses
                {
                    ["200"] = JsonResponse(Object(new()
                    {
                        ["from_formats"] = StringArray(),
                        ["to_formats"] = StringArray(),
                        ["sources"] = StringArray(),
                        ["options"] = StringArray(),
                        ["gotenberg_enabled"] = new OpenApiSchema { Type = JsonSchemaType.Boolean },
                        ["async_support"] = new OpenApiSchema { Type = JsonSchemaType.Boolean },
                        ["limits"] = Object(new()
                        {
                            ["max_file_size"] = new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int64" },
                            ["max_num_files"] = new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int32" }
                        }, "max_file_size", "max_num_files")
                    }, "from_formats", "to_formats", "sources", "options", "gotenberg_enabled", "async_support", "limits"), "Configured server capabilities.")
                };
                break;
            case "v1/status/poll/{task_id}":
                operation.OperationId = "pollTask";
                operation.Summary = "Get background task status and queue position";
                operation.Responses = new OpenApiResponses
                {
                    ["200"] = JsonResponse(TaskStatus(), "Current task status. Position is set only while pending."),
                    ["404"] = Problem("Task not found or expired.", context)
                };
                break;
            case "v1/result/{task_id}":
                ConversionResponses(operation, context);
                operation.OperationId = "getTaskResult";
                operation.Summary = "Retrieve a completed task result";
                operation.Description = "Returns the same JSON/ZIP as synchronous conversion. Results can be downloaded repeatedly until expiration. "
                    + "Failed tasks return Problem Details with the conversion error status. Retrieval does not delete the result.";
                operation.Responses!["404"] = Problem("Task not found or expired.", context);
                operation.Responses["409"] = Problem("Task is pending or started.", context);
                operation.Responses["504"] = Problem("HTTP source download timed out.", context);
                break;
            case "v1/status/ws/{task_id}":
                operation.OperationId = "watchTask";
                operation.Summary = "WebSocket task status updates (requires Upgrade)";
                operation.Description = "Connect with ws:// or wss://, not a regular HTTP GET. "
                    + "The first text message contains the current task status; later messages use the same schema as polling. "
                    + "The server closes the socket after success/failure. Disconnecting does not cancel the task. "
                    + "X-Api-Key is required during upgrade when configured; browser native WebSocket cannot set custom headers. "
                    + "Swagger UI does not execute WebSocket sessions.";
                operation.Responses = new OpenApiResponses
                {
                    ["101"] = new OpenApiResponse { Description = "WebSocket accepted. Text messages contain task_id, task_status, task_position, task_meta." },
                    ["400"] = Problem("WebSocket upgrade required.", context),
                    ["404"] = Problem("Task not found or expired.", context),
                    ["503"] = Problem("WebSocket connection limit reached.", context)
                };
                break;
        }

        if (path is "v1/convert/file/async" or "v1/convert/source/async")
        {
            operation.OperationId += "Async";
            operation.Summary = "Submit a background conversion task";
            operation.Description = "Upload and request validation finish before acceptance. Conversion and source downloads run in a bounded background queue. "
                + "Disconnecting after acceptance does not cancel the task. Poll status or use WebSocket, then retrieve /v1/result/{task_id}. "
                + "Tasks and their retained disk results expire and are lost on restart. Queue/task capacity exhaustion returns 503.";
            operation.Responses!["200"] = JsonResponse(TaskStatus(), "Accepted task, initially pending.");
            operation.Responses.Remove("504");
            operation.Responses["503"] = Problem("The queue/retained task limit is full, or the server is stopping.", context);
        }

        if (path?.StartsWith("v1/", StringComparison.OrdinalIgnoreCase) == true && authentication.Value.ApiKey.Length > 0)
        {
            operation.Security = [new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("ApiKey", context.Document)] = []
            }];
            operation.Responses ??= new OpenApiResponses();
            operation.Responses["401"] = Problem("Missing, invalid or multiple X-Api-Key header values.", context);
        }
    }

    private static void ConversionResponses(OpenApiOperation operation, OperationFilterContext context)
    {
        operation.Description = "One document returns a JSON envelope; multiple documents return a ZIP of ordinary output files. "
            + "JSON content uses the FlexyParse model, not DoclingDocument. Unknown options are rejected. "
            + "Each requested format runs the existing converter separately. Disconnecting cancels processing.";
        operation.Responses = new OpenApiResponses
        {
            ["200"] = new OpenApiResponse
            {
                Description = "Converted document or ZIP archive. Only requested document content fields are included.",
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/json"] = new()
                    {
                        Schema = Object(new()
                        {
                            ["document"] = Object(new()
                            {
                                ["md_content"] = new OpenApiSchema { Type = JsonSchemaType.String },
                                ["json_content"] = new OpenApiSchema
                                {
                                    Type = JsonSchemaType.Object,
                                    AdditionalPropertiesAllowed = true,
                                    Description = "FlexyParse JSON with sourceFileName, metadata and pages. Not DoclingDocument."
                                },
                                ["html_content"] = new OpenApiSchema { Type = JsonSchemaType.String }
                            }),
                            ["status"] = new OpenApiSchema { Type = JsonSchemaType.String, Enum = [JsonValue.Create("success")!] },
                            ["processing_time"] = new OpenApiSchema { Type = JsonSchemaType.Number, Format = "double", Description = "Processing time in seconds." },
                            ["timings"] = Object(new()),
                            ["errors"] = new OpenApiSchema { Type = JsonSchemaType.Array, Items = new OpenApiSchema(), MaxItems = 0 }
                        }, "document", "status", "processing_time", "timings", "errors")
                    },
                    ["application/zip"] = new() { Schema = Binary() }
                }
            },
            ["400"] = Problem("Invalid request, unsupported option, invalid Base64, inaccessible/forbidden URL or invalid document.", context),
            ["413"] = Problem("Document or bounded request body/field exceeds the configured limit.", context),
            ["499"] = Problem("Processing was cancelled, if the connection still permits a response.", context),
            ["503"] = Problem("Required model key is missing or Gotenberg is unavailable.", context),
            ["500"] = new OpenApiResponse { Description = "Unexpected server error. Production responses do not expose exception details." }
        };
    }

    private static OpenApiResponse Problem(string description, OperationFilterContext context) => new()
    {
        Description = description,
        Content = new Dictionary<string, OpenApiMediaType>
        {
            ["application/problem+json"] = new() { Schema = context.SchemaGenerator.GenerateSchema(typeof(ProblemDetails), context.SchemaRepository) }
        }
    };

    private static OpenApiResponse JsonResponse(OpenApiSchema schema, string description) => new()
    {
        Description = description,
        Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = new() { Schema = schema } }
    };

    private static OpenApiSchema Object(Dictionary<string, IOpenApiSchema> properties, params string[] required) => new()
    {
        Type = JsonSchemaType.Object,
        Properties = properties,
        Required = new HashSet<string>(required),
        AdditionalPropertiesAllowed = false
    };

    private static OpenApiSchema Binary() => new() { Type = JsonSchemaType.String, Format = "binary" };
    private static OpenApiSchema OcrLanguage() => new()
    {
        Type = JsonSchemaType.String,
        MinLength = 1,
        Example = JsonValue.Create("eslav"),
        Description = "Optional explicit RapidOCR model prefix. " + FlexyParse.Recognition.OcrLanguageProfiles.ModelLanguagesDescription + " "
            + "Only model prefixes are accepted; language aliases and regional tags are rejected. "
            + "ch selects mobile recognition; ch_server selects server recognition. en selects the dedicated English model. "
            + "The default environment setting is FLEXYPARSE_RAPIDOCR_LANGUAGE. "
            + "Omission uses Recognition:Language (eslav by default). One language applies to the entire batch. "
            + "Invalid languages return 400 before queuing. This selects a local RapidOCR profile, not automatic language detection or a provider override."
    };
    private static OpenApiSchema TaskStatus() => Object(new()
    {
        ["task_id"] = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" },
        ["task_status"] = new OpenApiSchema
        {
            Type = JsonSchemaType.String,
            Enum = [JsonValue.Create("pending")!, JsonValue.Create("started")!, JsonValue.Create("success")!, JsonValue.Create("failure")!]
        },
        ["task_position"] = new OpenApiSchema { Type = JsonSchemaType.Integer | JsonSchemaType.Null, Description = "One-based position while pending; otherwise null." },
        ["task_meta"] = new OpenApiSchema { Type = JsonSchemaType.Object | JsonSchemaType.Null, Description = "Reserved; currently null." }
    }, "task_id", "task_status", "task_position", "task_meta");
    private static OpenApiSchema StringArray() => new() { Type = JsonSchemaType.Array, Items = new OpenApiSchema { Type = JsonSchemaType.String } };

    private static OpenApiSchema Formats() => new()
    {
        Type = JsonSchemaType.Array,
        Items = new OpenApiSchema { Type = JsonSchemaType.String, Enum = [JsonValue.Create("md")!, JsonValue.Create("json")!, JsonValue.Create("html")!] },
        MinItems = 1,
        MaxItems = 3,
        Default = new JsonArray("md"),
        Description = "Requested output formats. For multipart, repeat the to_formats field."
    };

    private static OpenApiSchema Ocr() => new()
    {
        Type = JsonSchemaType.Boolean,
        Default = JsonValue.Create(true),
        Description = "Recognize uploaded raster images or extracted PDF images using the configured vision model. Raster uploads bypass PDF conversion. Not full-page PDF OCR. Ignored for Markdown."
    };
}
