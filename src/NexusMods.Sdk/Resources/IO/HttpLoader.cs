using System.Diagnostics;
using JetBrains.Annotations;

namespace NexusMods.Sdk.Resources;

[PublicAPI]
public sealed class HttpLoader : IResourceLoader<Uri, byte[]>
{
    private readonly HttpClient _httpClient;
    private const string SupportedScheme = "https";
    private const long MaxBytes = 20 * 1024 * 1024;

    /// <summary>
    /// Constructor.
    /// </summary>
    public HttpLoader(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <inheritdoc/>
    public async ValueTask<Resource<byte[]>> LoadResourceAsync(Uri resourceIdentifier, CancellationToken cancellationToken)
    {
        Debug.Assert(resourceIdentifier.Scheme.Equals(SupportedScheme, StringComparison.OrdinalIgnoreCase));
        // Afterburn: enforce HTTPS in release builds too, and never read more than MaxBytes.
        if (!resourceIdentifier.IsAbsoluteUri || !resourceIdentifier.Scheme.Equals(SupportedScheme, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only HTTPS resources can be loaded");

        using var responseMessage = await _httpClient.GetAsync(resourceIdentifier, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        responseMessage.EnsureSuccessStatusCode();

        using var content = responseMessage.Content;
        if (content.Headers.ContentLength > MaxBytes) throw new InvalidDataException("Resource is too large");

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxBytes) throw new InvalidDataException("Resource is too large");
            buffer.Write(chunk, 0, read);
        }

        var bytes = buffer.ToArray();

        return new Resource<byte[]>
        {
            Data = bytes,
            ExpiresAt = GetExpiresAt(responseMessage),
        };
    }

    private static DateTime GetExpiresAt(HttpResponseMessage responseMessage)
    {
        var cacheControl = responseMessage.Headers.CacheControl;
        if (cacheControl is null) return DateTime.MaxValue;

        var maxAge = cacheControl.MaxAge;
        if (!maxAge.HasValue) return DateTime.MaxValue;

        var age = responseMessage.Headers.Age;

        var diff = maxAge.Value;
        if (age.HasValue) diff -= age.Value;

        return DateTime.UtcNow + diff;
    }
}
