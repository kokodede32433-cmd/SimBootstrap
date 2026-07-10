using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SimBootstrap.Contracts;

namespace SimBootstrap.Engine;

public interface IDownloadManager
{
    Task<DownloadResult> DownloadAsync(DownloadRequest request, CancellationToken cancellationToken = default);
    DownloadSession? GetSession(string sessionId);
    IEnumerable<DownloadSession> GetAllSessions();
}

public interface IDownloadProvider
{
    Task<DownloadResult> DownloadAsync(
        DownloadRequest request,
        Action<DownloadProgress> onProgress,
        CancellationToken cancellationToken = default
    );
}

public interface IIntegrityVerifier
{
    IntegrityCheckResult Verify(string filePath, string? expectedSha256, long? expectedSize);
}

public interface IDownloadSessionStore
{
    void Save(DownloadSession session);
    DownloadSession? Get(string sessionId);
    IEnumerable<DownloadSession> GetAll();
}
