using System.Text;
using Microsoft.Extensions.Time.Testing;
using PacketReady.Infrastructure.Blob;
using Xunit;

namespace PacketReady.Tests.Infrastructure.Blob;

public class LocalFileBlobStoreTests : IDisposable
{
    private readonly string _root;
    private readonly FakeTimeProvider _clock;
    private readonly LocalFileBlobStore _store;

    public LocalFileBlobStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "packetready-blob-tests-" + Guid.NewGuid().ToString("N"));
        _clock = new FakeTimeProvider(new DateTimeOffset(2026, 5, 22, 12, 0, 0, TimeSpan.Zero));
        _store = new LocalFileBlobStore(new BlobStoreOptions { RootPath = _root }, _clock);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static MemoryStream PdfBytes(string payload = "%PDF-1.4 hello world") =>
        new(Encoding.ASCII.GetBytes(payload));

    [Fact]
    public async Task PutAsync_WritesUnderYearMonthShard_AndReturnsFileUri()
    {
        var uri = await _store.PutAsync(PdfBytes(), "license.pdf", "application/pdf", CancellationToken.None);

        Assert.StartsWith("file://", uri);
        // Year/month shard from the fake clock (2026-05).
        var shard = Path.Combine(_root, "2026", "05");
        Assert.True(Directory.Exists(shard));
        var written = Directory.GetFiles(shard);
        Assert.Single(written);
        Assert.EndsWith(".pdf", written[0]);
    }

    [Fact]
    public async Task PutAsync_PreservesContentBytes()
    {
        var payload = "%PDF-1.4 some content here";
        var uri = await _store.PutAsync(PdfBytes(payload), "x.pdf", "application/pdf", CancellationToken.None);

        await using var read = await _store.GetAsync(uri, CancellationToken.None);
        using var sr = new StreamReader(read);
        var roundTripped = await sr.ReadToEndAsync();
        Assert.Equal(payload, roundTripped);
    }

    [Fact]
    public async Task PutAsync_GeneratesUniqueIds_AcrossMultipleCalls()
    {
        var u1 = await _store.PutAsync(PdfBytes("a"), "a.pdf", "application/pdf", CancellationToken.None);
        var u2 = await _store.PutAsync(PdfBytes("b"), "b.pdf", "application/pdf", CancellationToken.None);
        Assert.NotEqual(u1, u2);
    }

    [Fact]
    public async Task PutAsync_StripsUnsafeExtension()
    {
        // Crafted filename — the implementation should refuse "extensions" that
        // aren't ASCII alphanumeric.
        var uri = await _store.PutAsync(PdfBytes(), "weird.../etc/passwd", "application/octet-stream", CancellationToken.None);

        // The stored file has no extension when the input was a path-traversal
        // attempt, but the file IS written safely under the shard.
        var written = Directory.GetFiles(Path.Combine(_root, "2026", "05"));
        Assert.Single(written);
        Assert.DoesNotContain("passwd", written[0]);

        // GetAsync still round-trips.
        await using var s = await _store.GetAsync(uri, CancellationToken.None);
        Assert.NotNull(s);
    }

    [Fact]
    public async Task PutAsync_StripsOverlyLongExtension()
    {
        // SafeExtension caps the extension at 16 chars (including the leading dot).
        // Anything past that gets stripped — keeps a crafted "extension" from
        // bloating filenames or smuggling separators past the alnum filter.
        var uri = await _store.PutAsync(
            PdfBytes(), "report.thisextensionistoolong", "application/pdf", CancellationToken.None);

        var written = Directory.GetFiles(Path.Combine(_root, "2026", "05"));
        Assert.Single(written);
        Assert.DoesNotContain("thisextension", written[0]);
        Assert.EndsWith(Path.GetFileNameWithoutExtension(written[0]), Path.GetFileName(written[0]));
    }

    [Fact]
    public async Task PutAsync_DerivesExtensionFromOriginalName_NotMime()
    {
        var uri = await _store.PutAsync(PdfBytes(), "report.PDF", "application/something-else", CancellationToken.None);
        // Lowercase per SafeExtension; uses originalName, not mimeType.
        Assert.EndsWith(".pdf", uri);
    }

    [Fact]
    public async Task GetAsync_ThrowsOnNonExistent()
    {
        var bogus = new Uri(Path.Combine(_root, "2026", "05", "nonexistent.pdf")).AbsoluteUri;
        await Assert.ThrowsAsync<FileNotFoundException>(() => _store.GetAsync(bogus, CancellationToken.None));
    }

    [Fact]
    public async Task GetAsync_RejectsUriOutsideRoot()
    {
        // Build a file URI that points outside the configured root. The store
        // must refuse rather than read arbitrary filesystem paths — defense
        // in depth even though URIs only come from our DB rows in normal flow.
        var outsidePath = Path.Combine(Path.GetTempPath(), "definitely-not-our-root", "x.pdf");
        var outside = new Uri(outsidePath).AbsoluteUri;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _store.GetAsync(outside, CancellationToken.None));
    }

    [Fact]
    public async Task GetAsync_RejectsSiblingPrefixOfRoot()
    {
        // Sibling-prefix attack: if RootPath is "/tmp/packetready-blob-tests-AAA",
        // a path under "/tmp/packetready-blob-tests-AAA-evil/..." shares the prefix
        // but is NOT inside root. The trailing-separator check in ResolveAndValidate
        // exists specifically to reject this — pin it with a test so a future
        // refactor that drops the trailing separator surfaces here.
        var siblingPath = Path.Combine(_root + "-evil", "x.pdf");
        var sibling = new Uri(siblingPath).AbsoluteUri;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _store.GetAsync(sibling, CancellationToken.None));
    }

    [Fact]
    public async Task GetAsync_RejectsNonFileUri()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _store.GetAsync("s3://bucket/key.pdf", CancellationToken.None));
    }

    [Fact]
    public async Task GetAsync_RejectsRelativeUri()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _store.GetAsync("relative/path.pdf", CancellationToken.None));
    }

    [Fact]
    public void Constructor_CreatesRootIfMissing()
    {
        var fresh = Path.Combine(Path.GetTempPath(), "packetready-blob-fresh-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.False(Directory.Exists(fresh));
            _ = new LocalFileBlobStore(new BlobStoreOptions { RootPath = fresh }, _clock);
            Assert.True(Directory.Exists(fresh));
        }
        finally
        {
            if (Directory.Exists(fresh))
                Directory.Delete(fresh, recursive: true);
        }
    }

    [Fact]
    public void Constructor_RejectsBlankRootPath()
    {
        Assert.Throws<ArgumentException>(() => new LocalFileBlobStore(new BlobStoreOptions { RootPath = "" }, _clock));
    }
}
