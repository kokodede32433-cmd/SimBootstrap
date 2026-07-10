using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SimBootstrap.Contracts;

namespace SimBootstrap.Engine;

public class DownloadManager : IDownloadManager
{
    private readonly IDownloadProvider _provider;
    private readonly IDownloadSessionStore _store;
    private readonly IIntegrityVerifier _verifier;
    private readonly ILogger<DownloadManager> _logger;

    public DownloadManager(
        IDownloadProvider provider,
        IDownloadSessionStore store,
        IIntegrityVerifier verifier,
        ILogger<DownloadManager> logger)
    {
        _provider = provider;
        _store = store;
        _verifier = verifier;
        _logger = logger;
    }

    public async Task<DownloadResult> DownloadAsync(DownloadRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Url) || !Uri.TryCreate(request.Url, UriKind.Absolute, out _))
        {
            return new DownloadResult(false, request.DestinationPath, 0, "Invalid URL format.");
        }

        var session = new DownloadSession(request);
        _store.Save(session);

        int attempt = 0;
        DownloadResult? result = null;

        while (attempt <= request.MaxRetries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            session.Status = DownloadStatus.Downloading;
            _store.Save(session);

            _logger.LogInformation("Starting download attempt {Attempt}/{MaxRetries} for {Url}", attempt, request.MaxRetries, request.Url);

            result = await _provider.DownloadAsync(
                request,
                progress =>
                {
                    session.Progress = progress;
                    _store.Save(session);
                },
                cancellationToken
            );

            if (result.IsSuccess)
            {
                _logger.LogInformation("Download attempt succeeded. Verifying integrity of {Path}", request.DestinationPath);
                var integrity = _verifier.Verify(request.DestinationPath, request.ExpectedSha256, request.ExpectedSize);

                if (integrity.IsValid)
                {
                    session.Status = DownloadStatus.Completed;
                    session.CompletedAtUtc = DateTime.UtcNow;
                    _store.Save(session);
                    return result;
                }
                else
                {
                    _logger.LogError("Integrity check failed: {Error}", integrity.ErrorMessage);
                    session.Status = DownloadStatus.Failed;
                    session.ErrorMessage = integrity.ErrorMessage;
                    session.CompletedAtUtc = DateTime.UtcNow;
                    _store.Save(session);

                    // Do NOT retry automatically on integrity check failure
                    return new DownloadResult(false, request.DestinationPath, result.BytesDownloaded, integrity.ErrorMessage);
                }
            }

            attempt++;
            if (attempt <= request.MaxRetries)
            {
                _logger.LogWarning("Attempt {Attempt} failed: {Error}. Retrying...", attempt - 1, result.ErrorMessage);
                // Wait before retrying (backoff)
                await Task.Delay(1000 * attempt, cancellationToken);
            }
        }

        session.Status = DownloadStatus.Failed;
        session.ErrorMessage = result?.ErrorMessage ?? "Download failed.";
        session.CompletedAtUtc = DateTime.UtcNow;
        _store.Save(session);

        return result ?? new DownloadResult(false, request.DestinationPath, 0, "Download failed.");
    }

    public DownloadSession? GetSession(string sessionId) => _store.Get(sessionId);

    public IEnumerable<DownloadSession> GetAllSessions() => _store.GetAll();
}
