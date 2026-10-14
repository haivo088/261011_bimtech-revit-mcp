using System.IO;
using System.Text.Json;

namespace Bimtech.RevitMcp.Planning;

// Where plans are kept. JSON files for now; Extensible Storage could replace it later.
public interface IPlanStore
{
    void Save(Plan plan);

    // Returns null when the model has no plan with this ID.
    Plan? Load(PlanDocument document, string planId);
}

// One readable JSON file per plan, in PlanDocument.StorageFolder.
public sealed class JsonPlanStore : IPlanStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public void Save(Plan plan)
    {
        var folder = plan.Document.StorageFolder;
        if (string.IsNullOrEmpty(folder)) throw new InvalidOperationException("Plan has no storage folder.");
        Directory.CreateDirectory(folder);
        // Write a temporary file first so a crash never leaves a half-written plan.
        var path = PathFor(folder, plan.PlanId);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(plan, JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }

    public Plan? Load(PlanDocument document, string planId)
    {
        var path = PathFor(document.StorageFolder, planId);
        if (!File.Exists(path)) return null;
        var plan = JsonSerializer.Deserialize<Plan>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException($"Plan file is empty: {path}");
        if (plan.SchemaVersion > Plan.CurrentSchemaVersion)
            throw new InvalidDataException($"Plan file {path} was written by a newer add-in (schema {plan.SchemaVersion}).");
        plan.Document.StorageFolder = document.StorageFolder;
        return plan;
    }

    // PlanService validates planId, so it cannot contain path separators.
    private static string PathFor(string folder, string planId) => Path.Combine(folder, planId + ".json");
}
