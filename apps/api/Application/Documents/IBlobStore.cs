namespace PacketReady.Application.Documents;

/// <summary>
/// Persistence layer for raw document bytes. P3 emits <c>file:///…</c> URIs against
/// a local filesystem; P6 swaps in <c>s3://…</c>. Callers depend only on this
/// interface; the URI is opaque from the application layer.
///
/// <para>Writes are content-by-stream (PDFs can be tens of MB) so the implementation
/// can copy without materializing the whole blob in memory. Reads return a Stream
/// that the caller disposes — concrete implementations choose between file handle
/// and S3 response stream as the storage backend evolves.</para>
///
/// <para>Note: the URI returned by <see cref="PutAsync"/> is the implementation's
/// canonical form. For the local-file backend this is an absolute <c>file://</c>
/// URL, which couples the DB row to the deployment's filesystem layout — fine for
/// dev/single-host, but expect to rewrite or re-keyspace these rows during the P6
/// S3 cutover. Treat the URI as opaque; never parse it in application code.</para>
/// </summary>
public interface IBlobStore
{
    /// <summary>
    /// Persists <paramref name="content"/> and returns the canonical storage URI to
    /// store on <c>documents.storage_uri</c>. The implementation generates a fresh
    /// identifier — callers pass the original filename for extension hinting only.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="content"/> is null, or <paramref name="originalName"/> /
    /// <paramref name="mimeType"/> is blank.
    /// </exception>
    Task<string> PutAsync(
        Stream content,
        string originalName,
        string mimeType,
        CancellationToken ct);

    /// <summary>
    /// Opens a readable stream over the blob at <paramref name="storageUri"/>.
    /// Caller disposes the returned stream.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="storageUri"/> is blank, malformed, or uses an unsupported
    /// scheme for the active implementation.
    /// </exception>
    /// <exception cref="FileNotFoundException">The blob does not exist.</exception>
    /// <exception cref="UnauthorizedAccessException">
    /// The URI resolves outside the implementation's configured root.
    /// </exception>
    Task<Stream> GetAsync(string storageUri, CancellationToken ct);
}
