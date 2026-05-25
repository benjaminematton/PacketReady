namespace PacketReady.Domain.Documents;

/// <summary>
/// Who initiated the document upload. Stored as lowercase TEXT
/// (<c>'provider' | 'admin'</c>) on <c>documents.uploaded_by</c> via an explicit
/// value converter.
/// </summary>
public enum Uploader
{
    Provider,
    Admin,
}
