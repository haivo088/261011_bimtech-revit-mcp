using Bimtech.RevitMcp.Cad;
using Bimtech.RevitMcp.Commands;
using Bimtech.RevitMcp.Commands.Structural;
using Bimtech.RevitMcp.Recognition;

namespace Bimtech.RevitMcp.DevTools;

// Runs the recognition rules (addin/Recognition) on a live CAD import, the same code the tests run on saved
// cases. When the model already has grids, they are taken as the truth and compared, so a developer sees at
// once where the rules go wrong on this drawing.
public sealed class RunRecognitionCommand : IRevitCommand
{
    public string Method => "run_recognition";
    public CommandAccess Access => CommandAccess.Read;

    private const int MaxStylesReturned = 15;

    public object? Execute(CommandContext context)
    {
        var p = context.Params;
        var document = context.RequireDocument();
        var cad = StructuralJson.RequireCad(document, p.RequireElementId("cadId"), "cadId");
        var view = StructuralJson.RequirePlanView(context, document, cad);

        var data = CadExtractor.Extract(document, cad, view, new CadExtractor.Limits(300_000));
        var recognition = GridRecognizer.Recognize(data);
        var truth = p.GetBool("compareWithModel", true) ? CadExtractor.ModelGrids(document) : [];

        return new
        {
            source = data.Source,
            grids = recognition.Grids,
            chosenStyles = recognition.ChosenStyles.Select(index => data.Styles[index]),
            styleScores = recognition.Styles.Take(MaxStylesReturned),
            warnings = recognition.Warnings,
            comparison = truth.Count == 0 ? null : GridComparison.Compare(recognition.Grids, truth),
            note = truth.Count == 0
                ? "The model has no grids to compare with."
                : "Compared with the grids in the model, taken as the truth.",
        };
    }
}
