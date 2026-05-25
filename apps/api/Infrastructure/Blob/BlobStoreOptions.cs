namespace PacketReady.Infrastructure.Blob;

/// <summary>
/// Configuration for <see cref="LocalFileBlobStore"/>. Bound from
/// <c>BLOB_STORE_ROOT</c> at startup (env var or appsettings); defaults to
/// <c>&lt;api-content-root&gt;/blob-store</c> when unset so the dev box works
/// without explicit configuration.
/// </summary>
public sealed class BlobStoreOptions
{
    /// <summary>Absolute directory path where uploaded blobs land.</summary>
    public string RootPath { get; init; } = string.Empty;
}
