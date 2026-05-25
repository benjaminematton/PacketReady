using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using PacketReady.Application.Extraction.Extract;
using PacketReady.Domain.Documents;
using Xunit;

namespace PacketReady.Tests.Application.Extraction;

public class ExtractInMemoryCommandHandlerTests
{
    [Fact]
    public async Task Handle_ResolvesKeyedExtractor_AndReturnsResult()
    {
        var stubResult = new ExtractionResult(
            FieldsJson: """{"fullName":"Henry Anderson, MD"}""",
            FieldLocationsJson: """{"fullName":{"page":1,"bbox":[1,2,3,4]}}""",
            ConfidenceJson: """{"fullName":0.97}""",
            Model: "claude-sonnet-4-6",
            PromptHash: new string('a', 64),
            InputTokens: 5000,
            OutputTokens: 400);

        var extractor = new Mock<IDocTypeExtractor>(MockBehavior.Strict);
        extractor.SetupGet(x => x.DocType).Returns(DocType.License);
        extractor.Setup(x => x.ExtractAsync(It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(stubResult);

        var services = new ServiceCollection();
        services.AddKeyedSingleton(DocType.License, extractor.Object);
        var sp = services.BuildServiceProvider();

        var handler = new ExtractInMemoryCommandHandler(sp);

        var bytes = Encoding.ASCII.GetBytes("%PDF-1.4 stub");
        var result = await handler.Handle(
            new ExtractInMemoryCommand(bytes, DocType.License),
            CancellationToken.None);

        Assert.Same(stubResult, result);
        extractor.Verify(x => x.ExtractAsync(It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_ThrowsOnEmptyPdfBytes()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var handler = new ExtractInMemoryCommandHandler(services);

        await Assert.ThrowsAsync<ArgumentException>(() => handler.Handle(
            new ExtractInMemoryCommand(Array.Empty<byte>(), DocType.License),
            CancellationToken.None));
    }

    [Fact]
    public async Task Handle_ThrowsWhenNoExtractorRegisteredForDocType()
    {
        // Empty service collection: GetRequiredKeyedService surfaces an
        // InvalidOperationException. The endpoint's GetKeyedService probe is
        // supposed to catch this before we reach the handler — this guard
        // exists for the case where a wiring bug ships.
        var services = new ServiceCollection().BuildServiceProvider();
        var handler = new ExtractInMemoryCommandHandler(services);

        var bytes = Encoding.ASCII.GetBytes("%PDF");
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.Handle(
            new ExtractInMemoryCommand(bytes, DocType.Dea),
            CancellationToken.None));
    }
}
