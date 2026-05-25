using MediatR;
using Microsoft.Extensions.DependencyInjection;
using PacketReady.Domain.Documents;

namespace PacketReady.Application.Extraction.Extract;

/// <summary>
/// Path A — stateless extraction. Reads bytes in memory, dispatches to the
/// keyed <see cref="IDocTypeExtractor"/> for <paramref name="DocType"/>, returns
/// the <see cref="ExtractionResult"/>. No DB writes, no classifier call, no
/// idempotency cache (the eval runner already has the type in its
/// <c>golden.json</c>; classifier output would be wasted).
///
/// <para>PDF-only by construction; non-PDF intake is a P5 concern that lives on
/// the upload-portal endpoint, not this Path-A path.</para>
/// </summary>
public sealed record ExtractInMemoryCommand(
    byte[] PdfBytes,
    DocType DocType) : IRequest<ExtractionResult>;

public sealed class ExtractInMemoryCommandHandler
    : IRequestHandler<ExtractInMemoryCommand, ExtractionResult>
{
    private readonly IServiceProvider _services;

    // IServiceProvider rather than IKeyedServiceProvider so the same handler
    // works against the .NET 8+ keyed DI surface without an extra abstraction
    // layer. GetRequiredKeyedService is an extension on IServiceProvider.
    public ExtractInMemoryCommandHandler(IServiceProvider services)
    {
        _services = services;
    }

    public async Task<ExtractionResult> Handle(
        ExtractInMemoryCommand request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.PdfBytes is null || request.PdfBytes.Length == 0)
            throw new ArgumentException("PDF bytes are required.", nameof(request));

        // GetRequiredKeyedService throws InvalidOperationException for an
        // unregistered key — surfaces as a 500 at the endpoint. The endpoint
        // probes via GetKeyedService and returns a 400 for unregistered types,
        // so reaching here without a registration is a wiring bug, not a
        // client mistake.
        var extractor = _services.GetRequiredKeyedService<IDocTypeExtractor>(request.DocType);

        return await extractor.ExtractAsync(request.PdfBytes, cancellationToken);
    }
}
