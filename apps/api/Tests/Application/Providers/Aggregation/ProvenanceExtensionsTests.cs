using PacketReady.Application.Providers.Aggregation;
using PacketReady.Application.Scoring;
using PacketReady.Domain.Scoring;
using Xunit;

namespace PacketReady.Tests.Application.Providers.Aggregation;

public sealed class ProvenanceExtensionsTests
{
    private static FieldProvenance MakeProv(double confidence) =>
        new(DocumentId: Guid.NewGuid(),
            Page: 1,
            Bbox: new BoundingBox(0, 0, 100, 20),
            Confidence: confidence);

    [Fact]
    public void Cite_OnMiss_ReturnsCitationWithoutLowConfidenceFlag()
    {
        var prov = new Dictionary<string, FieldProvenance>();

        var citation = prov.Cite("test", "value", "license.expiryDate");

        Assert.False(citation.LowConfidence);
        Assert.Null(citation.DocumentId);
    }

    [Fact]
    public void Cite_OnHit_WithHighConfidence_DoesNotFlagLowConfidence()
    {
        var prov = new Dictionary<string, FieldProvenance>
        {
            ["license.expiryDate"] = MakeProv(confidence: 0.99),
        };

        var citation = prov.Cite("test", "value", "license.expiryDate");

        Assert.False(citation.LowConfidence);
        Assert.NotNull(citation.DocumentId);
    }

    [Fact]
    public void Cite_OnHit_AtExactlyThreshold_DoesNotFlag()
    {
        // Threshold is `< 0.85` strict — exactly 0.85 is high-confidence.
        // Mirrors the operator-facing contract; revisit only with data.
        var prov = new Dictionary<string, FieldProvenance>
        {
            ["license.expiryDate"] = MakeProv(confidence: ConfidenceGuard.CriticalEligibleThreshold),
        };

        var citation = prov.Cite("test", "value", "license.expiryDate");

        Assert.False(citation.LowConfidence);
    }

    [Fact]
    public void Cite_OnHit_BelowThreshold_FlagsLowConfidence()
    {
        var prov = new Dictionary<string, FieldProvenance>
        {
            ["license.expiryDate"] = MakeProv(confidence: 0.84),
        };

        var citation = prov.Cite("test", "value", "license.expiryDate");

        Assert.True(citation.LowConfidence);
        Assert.NotNull(citation.DocumentId);
    }

    [Fact]
    public void TryCite_ReportsHitMissAndFlagsLowConfidence()
    {
        var prov = new Dictionary<string, FieldProvenance>
        {
            ["license.expiryDate"] = MakeProv(confidence: 0.50),
        };

        var hit = prov.TryCite("test", "value", "license.expiryDate", out var citation);
        var miss = prov.TryCite("test", "value", "license.issueDate", out var missCitation);

        Assert.True(hit);
        Assert.True(citation.LowConfidence);

        Assert.False(miss);
        Assert.False(missCitation.LowConfidence);
        Assert.Null(missCitation.DocumentId);
    }
}
