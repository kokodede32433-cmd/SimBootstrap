using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SimBootstrap.Contracts;

namespace SimBootstrap.Engine;

public class HttpDownloadProvider : IDownloadProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<HttpDownloadProvider> _logger;

    public HttpDownloadProvider(HttpClient httpClient, ILogger<HttpDownloadProvider> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<DownloadResult> DownloadAsync(
        DownloadRequest request,
        Action<DownloadProgress> onProgress,
        CancellationToken cancellationToken = default)
    {
        var tempFilePath = request.DestinationPath + ".tmp";
        var fileMode = FileMode.Create;
        long startPosition = 0;

        if (File.Exists(tempFilePath))
        {
            startPosition = new FileInfo(tempFilePath).Length;
            if (startPosition > 0)
            {
                fileMode = FileMode.Append;
                _logger.LogInformation("Found partial download of size {Size} bytes. Attempting to resume.", startPosition);
            }
        }

        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, request.Url);
            if (startPosition > 0)
            {
                httpRequest.Headers.Range = new RangeHeaderValue(startPosition, null);
            }

            using var httpResponse = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            var statusCode = httpResponse.StatusCode;
            var isResume = statusCode == HttpStatusCode.PartialContent;

            if (startPosition > 0 && !isResume)
            {
                // Server didn't accept resume, start over
                _logger.LogWarning("Server did not support resume (Status: {Status}). Restarting download from scratch.", statusCode);
                fileMode = FileMode.Create;
                startPosition = 0;
            }
            else if (statusCode != HttpStatusCode.OK && statusCode != HttpStatusCode.PartialContent)
            {
                httpResponse.EnsureSuccessStatusCode(); // Throws exception for other status codes
            }

            var totalBytes = (httpResponse.Content.Headers.ContentLength ?? 0) + startPosition;

            // Create destination directory if not exists
            var destDir = Path.GetDirectoryName(request.DestinationPath);
            if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            using var networkStream = await httpResponse.Content.ReadAsStreamAsync(cancellationToken);
            using var fileStream = new FileStream(tempFilePath, fileMode, FileAccess.Write, FileShare.None);

            var buffer = new byte[81920]; // 80 KB
            int bytesRead;
            var totalBytesRead = startPosition;

            var stopwatch = Stopwatch.StartNew();

            while ((bytesRead = await networkStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                totalBytesRead += bytesRead;

                var elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
                double speed = 0;
                if (elapsedSeconds > 0)
                {
                    speed = (totalBytesRead - startPosition) / elapsedSeconds;
                }

                var percentage = totalBytes > 0 ? ((double)totalBytesRead / totalBytes) * 100 : 0;
                onProgress?.Invoke(new DownloadProgress(totalBytesRead, totalBytes, percentage, speed));
            }

            fileStream.Close();

            // Rename temp file to final destination
            if (File.Exists(request.DestinationPath))
            {
                File.Delete(request.DestinationPath);
            }
            File.Move(tempFilePath, request.DestinationPath);

            return new DownloadResult(true, request.DestinationPath, totalBytesRead, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Download failed from URL: {Url}", request.Url);
            return new DownloadResult(false, request.DestinationPath, 0, ex.Message);
        }
    }
}
