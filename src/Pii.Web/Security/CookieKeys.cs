using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.AspNetCore.DataProtection;

namespace Pii.Web.Security;

public static class CookieKeys
{
    public static void AddWorkspaceCookieKeys(this WebApplicationBuilder builder)
    {
        var protection = builder.Services.AddDataProtection().SetApplicationName("Pii.Web");
        if (builder.Environment.IsDevelopment()) return;
        if (!Uri.TryCreate(builder.Configuration["Pii:DataProtection:BlobUri"], UriKind.Absolute, out var blob)
            || blob.Scheme != Uri.UriSchemeHttps || !blob.IsDefaultPort
            || !blob.Host.EndsWith(".blob.core.windows.net", StringComparison.Ordinal)
            || blob.UserInfo.Length != 0 || blob.Query.Length != 0 || blob.Fragment.Length != 0
            || blob.Segments.Length != 3 || !blob.AbsolutePath.EndsWith(".xml", StringComparison.Ordinal))
            throw new InvalidOperationException("Pii:DataProtection:BlobUri must reference a private Azure Blob XML key ring using managed identity.");
        protection.PersistKeysToAzureBlobStorage(blob, new DefaultAzureCredential());
    }

    public static async Task EnsurePrivateAsync(IConfiguration configuration)
    {
        var blob = new Uri(configuration["Pii:DataProtection:BlobUri"]!);
        var container = new BlobContainerClient(new Uri(blob, "."), new DefaultAzureCredential());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var properties = await container.GetPropertiesAsync(cancellationToken: deadline.Token);
        if (properties.Value.PublicAccess != PublicAccessType.None)
            throw new InvalidOperationException("Production cookie key container must deny anonymous access.");
    }
}