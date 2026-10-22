using System.Collections.Concurrent;
using Aperture.Platform.Models;

namespace Aperture.Platform.Services;

public interface IBatchStorageService
{
    BatchUploadSessionResponse CreateUploadSession(BatchUploadSessionRequest request);
    string GenerateScratchKey(string batchId, string fileId, int stepIndex);
    string GenerateEphemeralUrl(string s3Key, string method, TimeSpan duration);
}

public class MockS3StorageService : IBatchStorageService
{
    private const string BaseStorageUrl = "https://aperture-media-vault.s3.amazonaws.com";

    public BatchUploadSessionResponse CreateUploadSession(BatchUploadSessionRequest request)
    {
        var batchId = $"batch_{Guid.NewGuid():N}";
        var targets = new List<PreSignedUploadTarget>();
        var expiresAt = DateTime.UtcNow.AddMinutes(30);

        foreach (var file in request.Files)
        {
            var fileId = $"img_{Guid.NewGuid():N}";
            var s3Key = $"raw/{request.OrganizationId}/{batchId}/{fileId}/{file.ClientFileName}";
            
            // Simulates AWS SigV4 Pre-signed S3 URL
            var preSignedUrl = $"{BaseStorageUrl}/{s3Key}?X-Amz-Algorithm=AWS4-HMAC-SHA256&X-Amz-Expires=1800&X-Amz-Signature=mock-sig-{Guid.NewGuid():N}";

            targets.Add(new PreSignedUploadTarget(
                FileId: fileId,
                ClientFileName: file.ClientFileName,
                S3Key: s3Key,
                UploadUrl: preSignedUrl,
                RequiredHeaders: new Dictionary<string, string>
                {
                    { "Content-Type", file.ContentType },
                    { "x-amz-server-side-encryption", "AES256" }
                },
                ExpiresAtUtc: expiresAt
            ));
        }

        return new BatchUploadSessionResponse(
            BatchId: batchId,
            OrganizationId: request.OrganizationId,
            TotalFiles: targets.Count,
            UploadTargets: targets,
            SessionExpiresAtUtc: expiresAt
        );
    }

    public string GenerateScratchKey(string batchId, string fileId, int stepIndex)
    {
        return $"scratch/{batchId}/{fileId}/step_{stepIndex}_{Guid.NewGuid():N}.png";
    }

    public string GenerateEphemeralUrl(string s3Key, string method, TimeSpan duration)
    {
        return $"{BaseStorageUrl}/{s3Key}?signedMethod={method}&expires={duration.TotalSeconds}&token={Guid.NewGuid():N}";
    }
}

public interface IMeteringService
{
    void RecordFilterExecution(string orgId, string filterId, string version, long durationMs);
    IReadOnlyList<MeteringEvent> GetLedger(string? organizationId = null);
}

public class EventBridgeMeteringService : IMeteringService
{
    private readonly ConcurrentBag<MeteringEvent> _ledger = new();
    private const decimal BaseRatePerMsUsd = 0.000002m; // ~$0.002 per second of compute
    private const decimal DeveloperRevenueSplit = 0.75m; // 75% to 3rd party dev

    public void RecordFilterExecution(string orgId, string filterId, string version, long durationMs)
    {
        var developerId = filterId.StartsWith("vendor-") ? filterId.Split('/')[0] : "aperture-core";
        var totalCost = durationMs * BaseRatePerMsUsd;
        var devPayout = developerId == "aperture-core" ? 0m : totalCost * DeveloperRevenueSplit;

        var evt = new MeteringEvent(
            EventId: $"evt_{Guid.NewGuid():N}",
            OrganizationId: orgId,
            DeveloperId: developerId,
            FilterId: filterId,
            Version: version,
            ExecutionDurationMs: durationMs,
            EstimatedCostUsd: Math.Round(totalCost, 4),
            DeveloperPayoutUsd: Math.Round(devPayout, 4),
            TimestampUtc: DateTime.UtcNow
        );

        _ledger.Add(evt);
    }

    public IReadOnlyList<MeteringEvent> GetLedger(string? organizationId = null)
    {
        return organizationId == null
            ? _ledger.ToList()
            : _ledger.Where(e => e.OrganizationId == organizationId).ToList();
    }
}

public interface IPipelineOrchestrator
{
    void RegisterBatch(BatchUploadSessionResponse session);
    Task StartPipelineAsync(string batchId, ExecutePipelineRequest request);
    BatchProgressResponse? GetStatus(string batchId);
}

public class StepFunctionsSimulatorOrchestrator : IPipelineOrchestrator
{
    private readonly IBatchStorageService _storageService;
    private readonly IMeteringService _meteringService;
    private readonly ConcurrentDictionary<string, BatchProgressResponse> _batches = new();

    public StepFunctionsSimulatorOrchestrator(
        IBatchStorageService storageService,
        IMeteringService meteringService)
    {
        _storageService = storageService;
        _meteringService = meteringService;
    }

    public void RegisterBatch(BatchUploadSessionResponse session)
    {
        var images = session.UploadTargets.Select(t => new ImageProcessingProgress(
            FileId: t.FileId,
            Status: "Pending",
            CurrentFilter: null,
            CompletedSteps: 0,
            TotalSteps: 0,
            LastScratchS3Key: t.S3Key,
            FinalS3Url: null,
            ErrorMessage: null
        )).ToList();

        var state = new BatchProgressResponse(
            BatchId: session.BatchId,
            OrganizationId: session.OrganizationId,
            Status: BatchProcessingStatus.Queued,
            TotalImages: session.TotalFiles,
            CompletedCount: 0,
            FailedCount: 0,
            CreatedAtUtc: DateTime.UtcNow,
            FinishedAtUtc: null,
            Images: images
        );

        _batches[session.BatchId] = state;
    }

    public Task StartPipelineAsync(string batchId, ExecutePipelineRequest request)
    {
        if (!_batches.TryGetValue(batchId, out var currentBatch))
        {
            throw new KeyNotFoundException($"Batch {batchId} not found.");
        }

        // Fire-and-forget background execution (simulates Step Functions Distributed Map execution)
        _ = Task.Run(async () =>
        {
            var totalSteps = request.Pipeline.Count;
            var updatedImages = currentBatch.Images.Select(img => img with { TotalSteps = totalSteps, Status = "Processing" }).ToList();

            _batches[batchId] = currentBatch with
            {
                Status = BatchProcessingStatus.Processing,
                Images = updatedImages
            };

            // Run processing concurrently across all images in the batch (like Step Functions Map)
            var tasks = updatedImages.Select(async image =>
            {
                var currentScratch = image.LastScratchS3Key;
                for (int stepIdx = 0; stepIdx < request.Pipeline.Count; stepIdx++)
                {
                    var filter = request.Pipeline[stepIdx];
                    
                    // Simulate step execution delay & telemetry
                    var simulatedDurationMs = Random.Shared.Next(150, 400);
                    await Task.Delay(simulatedDurationMs);

                    // Fault tolerance simulation: 2% simulated corrupted image failure
                    if (image.FileId.EndsWith("f") && stepIdx == 1)
                    {
                        return image with
                        {
                            Status = "Failed",
                            ErrorMessage = $"Filter {filter.FilterId} failed: Unsupported color profile or OOM.",
                            CompletedSteps = stepIdx
                        };
                    }

                    // Checkpointing: save intermediate output to ephemeral scratch path
                    currentScratch = _storageService.GenerateScratchKey(batchId, image.FileId, stepIdx + 1);

                    // Record metering event for billing & revenue share
                    _meteringService.RecordFilterExecution(
                        request.OrganizationId,
                        filter.FilterId,
                        filter.Version,
                        simulatedDurationMs
                    );
                }

                return image with
                {
                    Status = "Completed",
                    CompletedSteps = totalSteps,
                    LastScratchS3Key = currentScratch,
                    FinalS3Url = $"https://aperture-media-vault.s3.amazonaws.com/final/{batchId}/{image.FileId}.png",
                    ErrorMessage = null
                };
            });

            var processedImages = (await Task.WhenAll(tasks)).ToList();
            var completedCount = processedImages.Count(i => i.Status == "Completed");
            var failedCount = processedImages.Count(i => i.Status == "Failed");

            _batches[batchId] = _batches[batchId] with
            {
                Status = failedCount > 0 ? BatchProcessingStatus.CompletedWithErrors : BatchProcessingStatus.Completed,
                CompletedCount = completedCount,
                FailedCount = failedCount,
                FinishedAtUtc = DateTime.UtcNow,
                Images = processedImages
            };
        });

        return Task.CompletedTask;
    }

    public BatchProgressResponse? GetStatus(string batchId)
    {
        _batches.TryGetValue(batchId, out var status);
        return status;
    }
}
