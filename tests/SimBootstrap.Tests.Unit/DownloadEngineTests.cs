using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SimBootstrap.Contracts;
using SimBootstrap.Engine;
using Xunit;

namespace SimBootstrap.Tests.Unit;

public class DownloadEngineTests
{
    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public MockHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _handler(request);
        }
    }

    [Fact]
    public async Task DownloadAsync_ShouldSucceed_WhenServerReturnsOk()
    {
        // Arrange
        var testContent = "Hello, SimBootstrap Download Engine!";
        var testBytes = Encoding.UTF8.GetBytes(testContent);
        var expectedSha256 = ComputeSha256(testBytes);

        var httpHandler = new MockHttpMessageHandler(req =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(testBytes)
            };
            return Task.FromResult(res);
        });

        var httpClient = new HttpClient(httpHandler);
        var provider = new HttpDownloadProvider(httpClient, NullLogger<HttpDownloadProvider>.Instance);
        var store = new InMemoryDownloadSessionStore();
        var verifier = new IntegrityVerifier();
        var manager = new DownloadManager(provider, store, verifier, NullLogger<DownloadManager>.Instance);

        var tempFile = Path.GetTempFileName();
        try
        {
            var request = new DownloadRequest(
                "https://example.com/file.txt",
                tempFile,
                expectedSha256,
                testBytes.Length
            );

            // Act
            var result = await manager.DownloadAsync(request);

            // Assert
            Assert.True(result.IsSuccess);
            Assert.Equal(tempFile, result.FilePath);
            Assert.Equal(testBytes.Length, result.BytesDownloaded);

            var fileContent = await File.ReadAllTextAsync(tempFile);
            Assert.Equal(testContent, fileContent);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task DownloadAsync_ShouldFail_WhenSha256Mismatch()
    {
        // Arrange
        var testBytes = Encoding.UTF8.GetBytes("Bad Data");
        var expectedSha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        var httpHandler = new MockHttpMessageHandler(req =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(testBytes)
            });
        });

        var httpClient = new HttpClient(httpHandler);
        var provider = new HttpDownloadProvider(httpClient, NullLogger<HttpDownloadProvider>.Instance);
        var store = new InMemoryDownloadSessionStore();
        var verifier = new IntegrityVerifier();
        var manager = new DownloadManager(provider, store, verifier, NullLogger<DownloadManager>.Instance);

        var tempFile = Path.GetTempFileName();
        try
        {
            var request = new DownloadRequest("https://example.com/file.txt", tempFile, expectedSha256);

            // Act
            var result = await manager.DownloadAsync(request);

            // Assert
            Assert.False(result.IsSuccess);
            Assert.Contains("Hash mismatch", result.ErrorMessage);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task DownloadAsync_ShouldFail_WhenUrlIsInvalid()
    {
        // Arrange
        var provider = new HttpDownloadProvider(new HttpClient(), NullLogger<HttpDownloadProvider>.Instance);
        var store = new InMemoryDownloadSessionStore();
        var verifier = new IntegrityVerifier();
        var manager = new DownloadManager(provider, store, verifier, NullLogger<DownloadManager>.Instance);

        var request = new DownloadRequest("invalid-url-string", "dummy-path");

        // Act
        var result = await manager.DownloadAsync(request);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal("Invalid URL format.", result.ErrorMessage);
    }

    [Fact]
    public async Task DownloadAsync_ShouldEmitProgressEvents()
    {
        // Arrange
        var testBytes = new byte[100000];
        Array.Fill(testBytes, (byte)0x42);

        var httpHandler = new MockHttpMessageHandler(req =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(testBytes)
            });
        });

        var httpClient = new HttpClient(httpHandler);
        var provider = new HttpDownloadProvider(httpClient, NullLogger<HttpDownloadProvider>.Instance);
        var store = new InMemoryDownloadSessionStore();
        var verifier = new IntegrityVerifier();
        var manager = new DownloadManager(provider, store, verifier, NullLogger<DownloadManager>.Instance);

        var tempFile = Path.GetTempFileName();
        try
        {
            var request = new DownloadRequest("https://example.com/large.bin", tempFile);

            // Act
            var result = await manager.DownloadAsync(request);

            // Assert
            Assert.True(result.IsSuccess);
            var sessions = store.GetAll();
            var session = Assert.Single(sessions);
            Assert.Equal(DownloadStatus.Completed, session.Status);
            Assert.True(session.Progress.BytesReceived > 0);
            Assert.Equal(100.0, session.Progress.Percentage);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task DownloadAsync_ShouldSupportCancellation()
    {
        // Arrange
        var testBytes = new byte[100000];
        var httpHandler = new MockHttpMessageHandler(req =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(testBytes)
            });
        });

        var httpClient = new HttpClient(httpHandler);
        var provider = new HttpDownloadProvider(httpClient, NullLogger<HttpDownloadProvider>.Instance);
        var store = new InMemoryDownloadSessionStore();
        var verifier = new IntegrityVerifier();
        var manager = new DownloadManager(provider, store, verifier, NullLogger<DownloadManager>.Instance);

        var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        var tempFile = Path.GetTempFileName();
        try
        {
            var request = new DownloadRequest("https://example.com/large.bin", tempFile);

            // Act & Assert
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                manager.DownloadAsync(request, cts.Token)
            );
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task DownloadAsync_ShouldRetryTransientErrors_AndThenFail()
    {
        // Arrange
        int callCount = 0;
        var httpHandler = new MockHttpMessageHandler(req =>
        {
            callCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        });

        var httpClient = new HttpClient(httpHandler);
        var provider = new HttpDownloadProvider(httpClient, NullLogger<HttpDownloadProvider>.Instance);
        var store = new InMemoryDownloadSessionStore();
        var verifier = new IntegrityVerifier();
        var manager = new DownloadManager(provider, store, verifier, NullLogger<DownloadManager>.Instance);

        var tempFile = Path.GetTempFileName();
        try
        {
            // Set MaxRetries = 2, so it will attempt 3 times in total (1 initial + 2 retries)
            var request = new DownloadRequest("https://example.com/error.bin", tempFile, MaxRetries: 2);

            // Act
            var result = await manager.DownloadAsync(request);

            // Assert
            Assert.False(result.IsSuccess);
            Assert.Equal(3, callCount);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task DownloadAsync_ShouldResume_WhenPartialFileExistsAndServerSupportsRange()
    {
        // Arrange
        var testContent = "FullContentOfFileAfterResume";
        var fullBytes = Encoding.UTF8.GetBytes(testContent);
        
        var partialContent = "FullContent";
        var partialBytes = Encoding.UTF8.GetBytes(partialContent);

        // Pre-create temp file containing the partial download
        var tempFile = Path.GetTempFileName();
        var tempFileTmp = tempFile + ".tmp";
        await File.WriteAllBytesAsync(tempFileTmp, partialBytes);

        var httpHandler = new MockHttpMessageHandler(req =>
        {
            var range = req.Headers.Range;
            Assert.NotNull(range);
            var rangeItem = Assert.Single(range.Ranges);
            Assert.Equal(partialBytes.Length, rangeItem.From);

            // Return partial content starting from index 11
            var remainingBytes = new byte[fullBytes.Length - partialBytes.Length];
            Array.Copy(fullBytes, partialBytes.Length, remainingBytes, 0, remainingBytes.Length);

            var res = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(remainingBytes)
            };
            return Task.FromResult(res);
        });

        var httpClient = new HttpClient(httpHandler);
        var provider = new HttpDownloadProvider(httpClient, NullLogger<HttpDownloadProvider>.Instance);
        var store = new InMemoryDownloadSessionStore();
        var verifier = new IntegrityVerifier();
        var manager = new DownloadManager(provider, store, verifier, NullLogger<DownloadManager>.Instance);

        try
        {
            var request = new DownloadRequest("https://example.com/resume.bin", tempFile);

            // Act
            var result = await manager.DownloadAsync(request);

            // Assert
            Assert.True(result.IsSuccess);
            var finalBytes = await File.ReadAllBytesAsync(tempFile);
            var finalString = Encoding.UTF8.GetString(finalBytes);
            Assert.Equal(testContent, finalString);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
            if (File.Exists(tempFileTmp)) File.Delete(tempFileTmp);
        }
    }

    private static string ComputeSha256(byte[] data)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(data);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }
}
