using Aperture.Platform.Models;
using Aperture.Platform.Services;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

// Register Core Services
builder.Services.AddSingleton<IBatchStorageService, MockS3StorageService>();
builder.Services.AddSingleton<IMeteringService, EventBridgeMeteringService>();
builder.Services.AddSingleton<IPipelineOrchestrator, StepFunctionsSimulatorOrchestrator>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new()
    {
        Title = "Aperture High-Scale Platform API",
        Version = "v1",
        Description = "Reference implementation for Aperture Bulk Processing Pipeline (Scenario A) and 3rd-Party Marketplace (Scenario B)."
    });
});

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Aperture Platform API v1");
    c.RoutePrefix = string.Empty; // Serve Swagger at app root
});

// -------------------------------------------------------------
// Scenario A: Bulk Ingest & Pipeline Orchestration
// -------------------------------------------------------------

// 1. Create Pre-Signed Upload Session (Direct S3 Ingest)
app.MapPost("/api/batches/upload-session", (
    [FromBody] BatchUploadSessionRequest request,
    [FromServices] IBatchStorageService storage,
    [FromServices] IPipelineOrchestrator orchestrator) =>
{
    if (request.Files == null || request.Files.Count == 0)
    {
        return Results.BadRequest("At least one file descriptor is required.");
    }

    var session = storage.CreateUploadSession(request);
    orchestrator.RegisterBatch(session);
    return Results.Ok(session);
})
.WithName("CreateBatchUploadSession")
.WithSummary("Generates direct-to-S3 pre-signed upload URLs for hundreds of images without touching backend proxy.")
.WithTags("Scenario A: Bulk Processing");

// 2. Trigger Multi-Step Filter Pipeline
app.MapPost("/api/batches/{batchId}/execute", async (
    string batchId,
    [FromBody] ExecutePipelineRequest request,
    [FromServices] IPipelineOrchestrator orchestrator) =>
{
    try
    {
        await orchestrator.StartPipelineAsync(batchId, request);
        return Results.Accepted($"/api/batches/{batchId}/status", new
        {
            Message = "Filter pipeline execution scheduled via Step Functions Distributed Map.",
            BatchId = batchId,
            StatusUrl = $"/api/batches/{batchId}/status"
        });
    }
    catch (KeyNotFoundException knf)
    {
        return Results.NotFound(knf.Message);
    }
})
.WithName("ExecuteBatchPipeline")
.WithSummary("Executes multi-step filter pipeline with intermediate S3 checkpointing and per-image retry.")
.WithTags("Scenario A: Bulk Processing");

// 3. Query Batch Execution Status (Low-overhead Polling)
app.MapGet("/api/batches/{batchId}/status", (
    string batchId,
    [FromServices] IPipelineOrchestrator orchestrator) =>
{
    var status = orchestrator.GetStatus(batchId);
    return status != null ? Results.Ok(status) : Results.NotFound($"Batch {batchId} not found.");
})
.WithName("GetBatchStatus")
.WithSummary("Fetches atomic batch completion progress and intermediate/final S3 artifacts.")
.WithTags("Scenario A: Bulk Processing");

// -------------------------------------------------------------
// Scenario B: 3rd-Party Marketplace & Sandboxing Contract
// -------------------------------------------------------------

// 4. Third-Party Sandboxed Execution Envelope (Zero-Trust S3 URLs)
app.MapPost("/api/marketplace/filters/invoke-sandbox", (
    [FromBody] FilterExecutionPayload payload,
    [FromServices] IMeteringService metering) =>
{
    // Simulates the isolated containerized Lambda executing on Firecracker
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    
    // Simulate image transformation inside RAM
    Thread.Sleep(120); 
    stopwatch.Stop();

    // Record asynchronous metering event
    metering.RecordFilterExecution(
        "org_enterprise_demo",
        payload.FilterId,
        payload.FilterVersion,
        stopwatch.ElapsedMilliseconds
    );

    return Results.Ok(new FilterExecutionResult(
        InvocationId: payload.InvocationId,
        Success: true,
        ExecutionDurationMs: stopwatch.ElapsedMilliseconds,
        OutputSizeBytes: 4_850_120, // ~4.85 MB
        ErrorMessage: null
    ));
})
.WithName("InvokeSandboxedFilter")
.WithSummary("Executes untrusted 3rd-party filter using ephemeral S3 pre-signed read/write URLs.")
.WithTags("Scenario B: Marketplace");

// 5. Query Metering & Revenue Split Ledger
app.MapGet("/api/marketplace/metering/ledger", (
    [FromQuery] string? organizationId,
    [FromServices] IMeteringService metering) =>
{
    var records = metering.GetLedger(organizationId);
    return Results.Ok(records);
})
.WithName("GetMeteringLedger")
.WithSummary("View metered compute records, platform cost, and 75/25 developer payout splits.")
.WithTags("Scenario B: Marketplace");

app.Run();
