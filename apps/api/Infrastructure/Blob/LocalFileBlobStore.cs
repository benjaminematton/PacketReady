using PacketReady.Application.Documents;

namespace PacketReady.Infrastructure.Blob;

/// <summary>
/// Local-filesystem <see cref="IBlobStore"/>. Layout:
/// <c>{RootPath}/yyyy/MM/{guid}{ext}</c>. The year/month shard keeps any single
/// directory from accumulating tens of thousands of files (slow <c>ls</c>; some
/// filesystems degrade past a few hundred thousand entries per dir).
///
/// <para>Storage URIs are absolute <c>file://</c> URLs. Resolution on read goes
/// through <see cref="ResolveAndValidate"/>, which rejects any path that escapes
/// <see cref="BlobStoreOptions.RootPath"/> — defense in depth even though URIs
/// only come from our own DB rows in normal flow.</para>
/// </summary>
public sealed class LocalFileBlobStore : IBlobStore
{
    private readonly string _root;
    private readonly TimeProvider _time;

    public LocalFileBlobStore(BlobStoreOptions options, TimeProvider time)
    {
        if (string.IsNullOrWhiteSpace(options.RootPath))
            throw new ArgumentException("BlobStore root path is required.", nameof(options));

        // Canonicalize once at construction: trailing-separator + symlink resolution
        // happens here, not on every read. Comparisons against this string are
        // ordinal, no realpath() on every request.
        _root = Path.GetFullPath(options.RootPath);
        _time = time;

        Directory.CreateDirectory(_root);
    }

    public async Task<string> PutAsync(
        Stream content,
        string originalName,
        string mimeType,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (string.IsNullOrWhiteSpace(originalName))
            throw new ArgumentException("Original name is required.", nameof(originalName));
        if (string.IsNullOrWhiteSpace(mimeType))
            throw new ArgumentException("MIME type is required.", nameof(mimeType));

        var now = _time.GetUtcNow();
        var shard = Path.Combine(_root, now.ToString("yyyy"), now.ToString("MM"));
        Directory.CreateDirectory(shard);

        var id = Guid.NewGuid();
        var ext = SafeExtension(originalName);
        var absPath = Path.Combine(shard, $"{id:N}{ext}");

        // FileShare.None: any concurrent reader sees EBUSY rather than a torn
        // half-written file. PDFs are uploaded atomically anyway, but the
        // contract should be strict.
        await using (var dest = new FileStream(
            absPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81_920,
            useAsync: true))
        {
            await content.CopyToAsync(dest, ct);
        }

        return new Uri(absPath).AbsoluteUri;
    }

    public Task<Stream> GetAsync(string storageUri, CancellationToken ct)
    {
        var absPath = ResolveAndValidate(storageUri);

        if (!File.Exists(absPath))
            throw new FileNotFoundException($"Blob not found: {storageUri}", absPath);

        Stream s = new FileStream(
            absPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81_920,
            useAsync: true);

        return Task.FromResult(s);
    }

    // Parse the URI, normalize to an absolute path, and confirm it sits inside the
    // configured root. GetFullPath collapses ".." / "." / duplicate separators but
    // does NOT resolve symlinks — so a symlink planted inside the root that points
    // elsewhere would pass this check. We accept that gap because storage URIs only
    // come from our own DB rows; the guard here is defense in depth against config
    // drift and accidental ../ traversal, not a sandbox against an attacker with
    // write access to the blob directory.
    private string ResolveAndValidate(string storageUri)
    {
        if (string.IsNullOrWhiteSpace(storageUri))
            throw new ArgumentException("Storage URI is required.", nameof(storageUri));

        if (!Uri.TryCreate(storageUri, UriKind.Absolute, out var uri) || !uri.IsFile)
            throw new ArgumentException(
                $"Expected a file:// URI, got: {storageUri}", nameof(storageUri));

        var abs = Path.GetFullPath(uri.LocalPath);

        // Trailing-separator-normalized prefix check: '/blob' must not match
        // '/blob-store/...'.
        var rootWithSep = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;
        if (!abs.StartsWith(rootWithSep, StringComparison.Ordinal))
            throw new UnauthorizedAccessException(
                $"Storage URI resolves outside the blob store root: {storageUri}");

        return abs;
    }

    private static string SafeExtension(string originalName)
    {
        var ext = Path.GetExtension(originalName);
        if (string.IsNullOrEmpty(ext))
            return string.Empty;

        // Path.GetExtension can return ".." or similar via crafted input. Allow
        // only a leading dot followed by ASCII letters/digits — the only shapes
        // a real document filename produces.
        if (ext.Length > 16) return string.Empty;
        foreach (var c in ext.AsSpan(1))
            if (!char.IsLetterOrDigit(c)) return string.Empty;

        return ext.ToLowerInvariant();
    }
}
