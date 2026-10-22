namespace Aperture.Platform.Models;

public record FileUploadDescriptor(
    string ClientFileName,
    long FileSizeBytes,
    string ContentType,
    string? ChecksumSha256 = null
);

public record BatchUploadSessionRequest(
    string OrganizationId,
    List<FileUploadDescriptor> Files
);

public record PreSignedUploadTarget(
    string FileId,
    string ClientFileName,
    string S3Key,
    string UploadUrl,
    Dictionary<string, string> RequiredHeaders,
    DateTime ExpiresAtUtc
);

public record BatchUploadSessionResponse(
    string BatchId,
    string OrganizationId,
    int TotalFiles,
    List<PreSignedUploadTarget> UploadTargets,
    DateTime SessionExpiresAtUtc
);

public record FilterPipelineStep(
    string FilterId,        // e.g. "aperture/grayscale" or "vendor-acme/hdr-enhancer"
    string Version,         // e.g. "1.2.0"
    Dictionary<string, object>? Parameters = null
);

public record ExecutePipelineRequest(
    string OrganizationId,
    List<FilterPipelineStep> Pipeline
);

public enum BatchProcessingStatus
{
    Queued,
    Processing,
    Completed,
    CompletedWithErrors,
    Failed
}

public record ImageProcessingProgress(
    string FileId,
    string Status, // "Pending", "Processing", "Completed", "Failed"
    string? CurrentFilter,
    int CompletedSteps,
    int TotalSteps,
    string? LastScratchS3Key,
    string? FinalS3Url,
    string? ErrorMessage
);

public record BatchProgressResponse(
    string BatchId,
    string OrganizationId,
    BatchProcessingStatus Status,
    int TotalImages,
    int CompletedCount,
    int FailedCount,
    DateTime CreatedAtUtc,
    DateTime? FinishedAtUtc,
    List<ImageProcessingProgress> Images
);

// Scenario B: Standard Execution Envelope for Sandboxed 3rd-Party Filters
public record FilterExecutionPayload(
    string InvocationId,
    string FilterId,
    string FilterVersion,
    InputResource Input,
    OutputResource Output,
    Dictionary<string, object>? Parameters
);

public record InputResource(
    string EphemeralDownloadUrl,
    string MimeType
);

public record OutputResource(
    string EphemeralUploadUrl,
    string MimeType
);

public record FilterExecutionResult(
    string InvocationId,
    bool Success,
    long ExecutionDurationMs,
    long OutputSizeBytes,
    string? ErrorMessage = null
);

public record MeteringEvent(
    string EventId,
    string OrganizationId,
    string DeveloperId,
    string FilterId,
    string Version,
    long ExecutionDurationMs,
    decimal EstimatedCostUsd,
    decimal DeveloperPayoutUsd,
    DateTime TimestampUtc
);
