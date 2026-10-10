using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Pii.Application;

namespace Pii.Infrastructure;

public static partial class ArtifactNames
{
    public static string Original(string hash, string extension) => Validate($"originals/{hash}.{extension}");
    public static string Report(string runId, int generation, string extension = "html") => Validate($"reports/{runId}-{generation}.{extension}");
    public static string Validate(string name)
    {
        if (name.Length > 240 || name.Contains("..", StringComparison.Ordinal) || !SafeName().IsMatch(name))
            throw new ArgumentException("Artifact must use a canonical workspace-relative name, never a URL or filesystem path.", nameof(name));
        return name;
    }
    [GeneratedRegex(@"\A(?:originals|imports|reports|runs|artifacts)/[a-z0-9][a-z0-9_-]*(?:/[a-z0-9][a-z0-9_-]*)?\.(?:csv|pdf|json|html|md|txt)\z", RegexOptions.CultureInvariant)]
    private static partial Regex SafeName();
}

public sealed class BlobArtifactStore(BlobContainerClient container, bool development = false) : IArtifactStore
{
    public const int MaximumBytes = 8 * 1024 * 1024;
    private const string Prefix = "workspace/";

    public async Task<string> WriteAsync(string name, ReadOnlyMemory<byte> bytes, string contentType, CancellationToken cancellation)
    {
        ArtifactNames.Validate(name);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Artifact exceeds 8 MiB.");
        if (contentType is not ("application/json" or "application/pdf" or "text/csv" or "text/html" or "text/plain" or "text/markdown"))
            throw new ArgumentException("Unsupported artifact content type.", nameof(contentType));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        await RequirePrivateAsync(deadline.Token);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes.Span));
        var blob = container.GetBlobClient(Prefix + name);
        try
        {
            await blob.UploadAsync(BinaryData.FromBytes(bytes), new BlobUploadOptions
            {
                Conditions = new() { IfNoneMatch = ETag.All },
                HttpHeaders = new() { ContentType = contentType },
                Metadata = new Dictionary<string, string> { ["sha256"] = hash }
            }, deadline.Token);
        }
        catch (RequestFailedException error) when (error.Status is 409 or 412)
        {
            var properties = (await blob.GetPropertiesAsync(cancellationToken: deadline.Token)).Value;
            if (!properties.Metadata.TryGetValue("sha256", out var existing) || existing != hash || properties.ContentType != contentType)
                throw new WorkspaceConflictException("Immutable artifact name already contains different content.");
        }
        return name;
    }

    public async Task<byte[]> ReadAsync(string name, CancellationToken cancellation)
    {
        ArtifactNames.Validate(name);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        await RequirePrivateAsync(deadline.Token);
        var response = (await container.GetBlobClient(Prefix + name).DownloadStreamingAsync(cancellationToken: deadline.Token)).Value;
        await using var stream = response.Content;
        if (response.Details.ContentLength > MaximumBytes) throw new InvalidDataException("Artifact exceeds 8 MiB.");
        return await BoundedBytes.ReadAsync(stream, MaximumBytes, deadline.Token);
    }

    public async Task DeleteAllAsync(CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        await RequirePrivateAsync(deadline.Token);
        var count = 0;
        await foreach (var blob in container.GetBlobsAsync(prefix: Prefix, cancellationToken: deadline.Token))
        {
            if (++count > 10000) throw new InvalidOperationException("Deletion capped at 10000 blobs; repeat explicit deletion to continue.");
            ArtifactNames.Validate(blob.Name[Prefix.Length..]);
            await container.DeleteBlobIfExistsAsync(blob.Name, DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: deadline.Token);
        }
    }

    private async Task RequirePrivateAsync(CancellationToken cancellation)
    {
        if (development) await container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellation);
        var properties = (await container.GetPropertiesAsync(cancellationToken: cancellation)).Value;
        if (properties.PublicAccess != PublicAccessType.None) throw new InvalidOperationException("Artifact container must deny anonymous Blob access.");
    }
}

public static class BoundedBytes
{
    public static async Task<byte[]> ReadAsync(Stream stream, int maximumBytes, CancellationToken cancellation)
    {
        using var result = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellation)) != 0)
        {
            if (result.Length + count > maximumBytes) throw new InvalidDataException("Response exceeds the configured byte limit.");
            result.Write(buffer, 0, count);
        }
        return result.ToArray();
    }
}

public sealed class DatabasePollingDispatch : IWorkDispatch
{
    public Task SignalAsync(string runId, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        return Task.CompletedTask;
    }
}