using System;

namespace SimBootstrap.Contracts;

public enum DownloadStatus
{
    Pending,
    Downloading,
    Completed,
    Failed,
    Cancelled
}

public record DownloadProgress(
    long BytesReceived,
    long TotalBytes,
    double Percentage,
    double SpeedBytesPerSecond
);

public record DownloadRequest(
    string Url,
    string DestinationPath,
    string? ExpectedSha256 = null,
    long? ExpectedSize = null,
    int MaxRetries = 3
);

public record DownloadResult(
    bool IsSuccess,
    string FilePath,
    long BytesDownloaded,
    string? ErrorMessage
);

public record IntegrityCheckResult(
    bool IsValid,
    string? ErrorMessage
);

public class DownloadSession
{
    public string SessionId { get; } = Guid.NewGuid().ToString();
    public DownloadRequest Request { get; }
    public DownloadStatus Status { get; set; } = DownloadStatus.Pending;
    public DownloadProgress Progress { get; set; } = new(0, 0, 0, 0);
    public DateTime StartedAtUtc { get; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }
    public string? ErrorMessage { get; set; }

    public DownloadSession(DownloadRequest request)
    {
        Request = request;
    }
}
