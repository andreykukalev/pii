using System.Net;
using System.Security.Cryptography;
using System.Text;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using Pii.Infrastructure;
using Xunit;

namespace Pii.Infrastructure.Tests;

public sealed class ArtifactTests
{
    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("originals/../secret.txt")]
    [InlineData("https://host/path.json")]
    [InlineData("C:/secret.csv")]
    [InlineData("reports/UPPER.html")]
    [InlineData("originals/%2e%2e.csv")]
    [InlineData("reports/run.exe")]
    public void CanonicalNamesRejectPathsUrlsAndTraversal(string name) => Assert.Throws<ArgumentException>(() => ArtifactNames.Validate(name));

    [Fact]
    public void GenerationArtifactsAreSeparated() => Assert.NotEqual(ArtifactNames.Report("run", 1), ArtifactNames.Report("run", 2));

    [Fact]
    public async Task StreamLimitChecksActualBytesAndCancellation()
    {
        Assert.Equal(new byte[16], await BoundedBytes.ReadAsync(new MemoryStream(new byte[16]), 16, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => BoundedBytes.ReadAsync(new MemoryStream(new byte[17]), 16, default));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BoundedBytes.ReadAsync(new MemoryStream(new byte[17]), 16, cancelled.Token));
    }

    [Fact]
    public async Task BlobRequiresPrivateContainerBeforeUpload()
    {
        using var transport = new RecordedHttp((_, _) => Properties(publicAccess: true));
        using var client = new HttpClient(transport);
        var store = Store(client);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteAsync("originals/test.csv", new byte[1], "text/csv", default));
        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public async Task BlobUploadUsesImmutableConditionalWriteAndWorkspacePrefix()
    {
        using var transport = new RecordedHttp((request, _) => request.Method == HttpMethod.Get ? Properties() : Properties(status: HttpStatusCode.Created));
        using var client = new HttpClient(transport);
        Assert.Equal("originals/test.csv", await Store(client).WriteAsync("originals/test.csv", Encoding.UTF8.GetBytes("test"), "text/csv", default));
        Assert.Equal(2, transport.Calls);
        Assert.EndsWith("/workspace/originals/test.csv", transport.Uris[1].AbsolutePath);
    }

    [Fact]
    public async Task ImmutableReplayChecksStoredHashAndContentType()
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("test")));
        using var transport = new RecordedHttp((request, _) =>
        {
            if (request.Method == HttpMethod.Put)
            {
                var conflict = RecordedHttp.Json("""{"error":{"code":"ConditionNotMet","message":"recorded conflict"}}""", HttpStatusCode.PreconditionFailed);
                conflict.Headers.Add("x-ms-error-code", "ConditionNotMet");
                return conflict;
            }
            return request.RequestUri!.AbsolutePath.Contains("/workspace/", StringComparison.Ordinal) ? Properties(hash: hash) : Properties();
        });
        using var client = new HttpClient(transport);
        var store = Store(client);
        Assert.Equal("originals/test.csv", await store.WriteAsync("originals/test.csv", Encoding.UTF8.GetBytes("test"), "text/csv", default));
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => store.WriteAsync("originals/test.csv", Encoding.UTF8.GetBytes("different"), "text/csv", default));
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => store.WriteAsync("originals/test.csv", Encoding.UTF8.GetBytes("test"), "text/plain", default));
    }

    private static BlobArtifactStore Store(HttpClient client)
    {
        var options = new BlobClientOptions { Transport = new HttpClientTransport(client) };
        options.Retry.MaxRetries = 0;
        return new(new BlobContainerClient(new Uri("https://recorded.blob.core.windows.net/test"), options));
    }
    private static HttpResponseMessage Properties(bool publicAccess = false, HttpStatusCode status = HttpStatusCode.OK, string? hash = null)
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent([]) };
        response.Headers.Add("ETag", "\"recorded-etag\"");
        response.Content.Headers.LastModified = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        if (publicAccess) response.Headers.Add("x-ms-blob-public-access", "blob");
        if (hash is not null)
        {
            response.Headers.Add("x-ms-meta-sha256", hash);
            response.Content.Headers.ContentType = new("text/csv");
        }
        return response;
    }
}