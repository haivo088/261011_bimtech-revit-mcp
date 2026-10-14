using System.Text.Json;
using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Planning.Actions;

namespace Bimtech.RevitMcp.Planning;

// Writes approved plan items into a Revit model (docs/04-architecture.md, principle 4):
//   - one TransactionGroup, so the person undoes the whole commit with one Ctrl+Z;
//   - one Transaction per item, so a failing item is rolled back alone and the rest are kept;
//   - Revit failures never open a dialog (nobody is at the keyboard): warnings are recorded and
//     dismissed, errors roll back that item and become its error message.
public sealed class RevitPlanWriter(Document document, PlanActions actions, string undoName) : IPlanItemWriter
{
    public IReadOnlyList<PlanItemOutcome> Apply(IReadOnlyList<PlanItem> items)
    {
        var outcomes = new List<PlanItemOutcome>(items.Count);
        using var group = new TransactionGroup(document, undoName);
        group.Start();
        try
        {
            foreach (var item in items) outcomes.Add(ApplyOne(item));

            if (outcomes.Any(outcome => outcome.Applied)) group.Assimilate();
            else group.RollBack();
            return outcomes;
        }
        catch
        {
            if (group.HasStarted() && !group.HasEnded()) group.RollBack();
            throw;
        }
    }

    private PlanItemOutcome ApplyOne(PlanItem item)
    {
        if (!actions.TryGet(item.Action, out var action))
            return Failed(item, $"The add-in does not know the action '{item.Action}'.");

        var failures = new FailureCollector();
        using var transaction = new Transaction(document, $"{item.Action} {item.ItemId}");
        var options = transaction.GetFailureHandlingOptions();
        options.SetFailuresPreprocessor(failures);
        options.SetClearAfterRollback(true);
        transaction.SetFailureHandlingOptions(options);

        transaction.Start();
        object result;
        try
        {
            result = action.Apply(document, PlanItemParams.Effective(item));
        }
        catch (Exception exception) when (exception is PlanActionException or Autodesk.Revit.Exceptions.ApplicationException)
        {
            transaction.RollBack();
            return Failed(item, exception.Message);
        }

        if (transaction.Commit() != TransactionStatus.Committed || failures.Errors.Count > 0)
        {
            if (transaction.HasStarted() && !transaction.HasEnded()) transaction.RollBack();
            return Failed(item, "Revit rejected the change: " + string.Join(" | ", failures.Errors));
        }

        return new PlanItemOutcome(item.ItemId, true, JsonSerializer.SerializeToElement(new
        {
            value = result,
            warnings = failures.Warnings,
        }));
    }

    private static PlanItemOutcome Failed(PlanItem item, string error) =>
        new(item.ItemId, false, JsonSerializer.SerializeToElement(new { error }));

    // Records failures instead of showing them: warnings are deleted, errors roll the transaction back.
    private sealed class FailureCollector : IFailuresPreprocessor
    {
        public List<string> Warnings { get; } = [];
        public List<string> Errors { get; } = [];

        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            var hasError = false;
            foreach (var failure in accessor.GetFailureMessages())
            {
                if (failure.GetSeverity() == FailureSeverity.Warning)
                {
                    Warnings.Add(failure.GetDescriptionText());
                    accessor.DeleteWarning(failure);
                }
                else
                {
                    Errors.Add(failure.GetDescriptionText());
                    hasError = true;
                }
            }
            return hasError ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
        }
    }
}
