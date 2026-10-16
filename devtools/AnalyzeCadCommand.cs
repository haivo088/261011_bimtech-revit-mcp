using Bimtech.RevitMcp.Cad;
using Bimtech.RevitMcp.Commands;
using Bimtech.RevitMcp.Commands.Structural;
using Bimtech.RevitMcp.Recognition;

namespace Bimtech.RevitMcp.DevTools;

// The fingerprint of one CAD import (CadFingerprintBuilder): per layer what it holds and its typical sizes, the
// most frequent texts and the best grid candidate. Small enough for Claude to read the whole drawing and propose
// which layer is what. Developer tool for now; moves to the user tools once proven on real drawings.
public sealed class AnalyzeCadCommand : IRevitCommand
{
    public string Method => "analyze_cad";
    public CommandAccess Access => CommandAccess.Read;

    public object? Execute(CommandContext context)
    {
        var p = context.Params;
        var document = context.RequireDocument();
        var cad = StructuralJson.RequireCad(document, p.RequireElementId("cadId"), "cadId");
        var view = StructuralJson.RequirePlanView(context, document, cad);
        var maxLayers = p.GetInt("maxLayers", 60, 1, 500);

        var data = CadExtractor.Extract(document, cad, view, new CadExtractor.Limits(300_000));
        var fingerprint = CadFingerprintBuilder.Build(data);

        return new
        {
            source = data.Source,
            fingerprint.ExtentMm,
            fingerprint.Totals,
            layerCount = fingerprint.Layers.Count,
            layers = fingerprint.Layers.Take(maxLayers),
            texts = fingerprint.Texts,
            fingerprint.GridCandidate,
            fingerprint.Skipped,
            note = "Sizes are in mm, rounded to 5 mm. Texts are the view's Text Notes; text inside the DWG is not readable.",
        };
    }
}
