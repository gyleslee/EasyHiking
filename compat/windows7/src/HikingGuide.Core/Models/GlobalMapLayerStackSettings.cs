namespace HikingGuide.Core.Models;

public sealed record GlobalMapLayerStackSettings
{
    public static readonly Guid GermanyId = Guid.Parse("90a2fae7-7063-4938-912e-4f85b31c650c");
    public Guid BaseSourceId { get; init; } = GermanyId;
    public double BaseOpacity { get; init; } = 1;
    public Guid? Overlay1SourceId { get; init; }
    public bool Overlay1Visible { get; init; }
    public double Overlay1Opacity { get; init; } = .60;
    public Guid? Overlay2SourceId { get; init; }
    public bool Overlay2Visible { get; init; }
    public double Overlay2Opacity { get; init; } = .60;
}
