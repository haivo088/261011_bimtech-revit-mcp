using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Bimtech.RevitMcp.Commands;
using Bimtech.RevitMcp.Commands.Common;
using Bimtech.RevitMcp.Commands.Plans;
using Bimtech.RevitMcp.Planning;
using Bimtech.RevitMcp.Protocol;

namespace Bimtech.RevitMcp.DevTools;

// Runs C# in Revit: a new snippet ("code") or a command-set script ("script" + "args").
//   write = false: runs inside a TransactionGroup that is always rolled back, so the model never changes, and returns
//                  the value at once (exploring, checking, measuring).
//   write = true:  runs once as a preview inside one Transaction, notes what it would create, change and delete, and
//                  rolls it all back; then saves a plan with one "run_code" item (purpose, preview, the whole code)
//                  and opens the review window. Only commit_plan, after the person approves, runs it for real
//                  (RunCodeAction), and only the exact code that was previewed.
// Revit offers no way to abort a running snippet, so it stops itself: QueryGuardInserter adds time checks to every
// loop and a depth limit to every function. Every run goes to the history file (CodeHistory).
public sealed class SendCodeCommand(DevToolsHost host) : IRevitCommand
{
    public string Method => "send_code";
    // Every model change is rolled back; write=true only saves a plan.
    public CommandAccess Access => CommandAccess.Read;

    private const int MaxListedChanges = 12;
    private const int MaxPreviewChars = 1500;

    public object? Execute(CommandContext context)
    {
        var p = context.Params;
        var root = p.GetString("commandSetRoot", 1000);
        var historyFile = p.GetString("historyFile", 1000);
        var scriptId = p.GetString("script", 120);
        var codeParam = p.GetString("code", 100_000);
        if ((scriptId is null) == (codeParam is null))
            throw RpcException.InvalidParams("code", "pass either code (a new snippet) or script (a command-set script), not both.");

        string code;
        JsonElement? args = p.GetJsonObject("args");
        var write = p.GetBool("write", false);
        var purpose = p.GetString("purpose", 300);
        if (scriptId is not null)
        {
            if (root is null) throw RpcException.InvalidParams("script", "the server sent no commandSetRoot.");
            try
            {
                var (info, scriptCode) = new CommandSetStore(root).Load(scriptId);
                code = scriptCode;
                args = CommandSetStore.BindArgs(info, args);
                write = p.Has("write") ? write : info.Write;
                purpose ??= info.Description;
            }
            catch (CommandSetException exception)
            {
                throw RpcException.InvalidParams("script", exception.Message);
            }
        }
        else code = codeParam!;
        if (write && string.IsNullOrWhiteSpace(purpose))
            throw RpcException.InvalidParams("purpose", "say in one sentence what the code changes; the reviewer reads it.");

        var maxResultChars = p.GetInt("maxResultChars", 50_000, 1_000, 1_000_000);
        // Below the server's request timeout, so the answer still reaches Claude.
        var timeoutSeconds = p.GetInt("timeoutSeconds", 30, 1, 110);
        var uiDocument = context.RequireUIDocument();
        var document = uiDocument.Document;
        if (document.IsModifiable)
            throw new RpcException(RpcErrorCodes.NotWritable, "Revit is inside another command or transaction. Finish it, then retry.");
        if (write && document.IsReadOnly)
            throw new RpcException(RpcErrorCodes.NotWritable, "The model is read-only.");

        var hash = CodeRunner.Hash(code);
        void Record(bool ok, string stage) => CodeHistory.Append(historyFile, new CodeHistory.Entry(
            DateTimeOffset.Now, write ? "preview" : "read", purpose, scriptId, write, ok, stage, hash, code, document.Title));

        var watch = Stopwatch.StartNew();
        var compiled = QueryCompiler.Compile(code);
        if (compiled.Run is null)
        {
            Record(false, "compile");
            return new { ok = false, stage = "compile", errors = compiled.Errors, compileMs = watch.ElapsedMilliseconds };
        }
        var compileMs = watch.ElapsedMilliseconds;

        var query = new QueryContext(context.UIApplication, uiDocument, context.Cancellation, args);
        var changes = new ChangeCollector();
        CodeRunner.Outcome outcome;
        string? commitError = null;
        using var guard = new FailureGuard(context.UIApplication.Application);
        using (var group = new TransactionGroup(document, write ? "BIMTECH send_code preview (rolled back)" : "BIMTECH send_code (rolled back)"))
        {
            group.Start();
            try
            {
                if (!write)
                    outcome = CodeRunner.Run(compiled.Run, query, TimeSpan.FromSeconds(timeoutSeconds), maxResultChars);
                else
                {
                    // One open Transaction, exactly as commit_plan will run it, committed so Revit checks the changes
                    // and reports them; the group rollback then undoes everything.
                    context.UIApplication.Application.DocumentChanged += changes.OnChanged;
                    try
                    {
                        using var transaction = new Transaction(document, "BIMTECH send_code preview");
                        transaction.Start();
                        outcome = CodeRunner.Run(compiled.Run, query, TimeSpan.FromSeconds(timeoutSeconds), maxResultChars);
                        if (outcome.Ok)
                        {
                            if (transaction.Commit() != TransactionStatus.Committed || guard.Errors.Count > 0)
                                commitError = "Revit refused the changes: " + string.Join(" | ", guard.Errors);
                        }
                        else if (transaction.HasStarted() && !transaction.HasEnded()) transaction.RollBack();
                    }
                    finally
                    {
                        context.UIApplication.Application.DocumentChanged -= changes.OnChanged;
                    }
                }
            }
            finally
            {
                if (group.HasStarted() && !group.HasEnded()) group.RollBack();
            }
        }
        var runMs = watch.ElapsedMilliseconds - compileMs;
        var stage = commitError is not null ? "commit" : outcome.Stage;
        var ok = outcome.Ok && commitError is null;
        Record(ok, stage);

        if (!write || !ok)
            return new
            {
                ok,
                stage,
                result = outcome.Result,
                resultTruncated = outcome.Truncated,
                log = query.Log,
                error = outcome.Error ?? commitError,
                preview = write ? changes.Summary() : null,
                revitErrors = guard.Errors,
                revitWarnings = guard.Warnings,
                compileMs,
                runMs,
                note = write
                    ? "The preview failed and every change was rolled back; no plan was created. Fix the code and send it again."
                    : "Every change the code made to the model was rolled back.",
            };

        var plan = CreatePlan(document, code, hash, args, purpose!, scriptId, timeoutSeconds, historyFile, changes, outcome, query.Log, guard.Warnings);
        host.Review.Show(plan, context.UIApplication);
        return new
        {
            ok,
            stage = "previewed",
            preview = changes.Summary(),
            result = outcome.Result,
            log = query.Log,
            revitWarnings = guard.Warnings,
            plan = PlanResults.Status(plan),
            reviewWindow = "opened",
            note = "Nothing was changed in the model: the preview was rolled back. Ask the user to read the code and the "
                + "preview in the review window and say when they are done, then call get_plan_status and commit_plan. "
                + "If this kind of job keeps coming back, propose saving it with command_set action \"save\".",
        };
    }

    private Plan CreatePlan(
        Document document, string code, string hash, JsonElement? args, string purpose, string? scriptId, int timeoutSeconds,
        string? historyFile, ChangeCollector changes, CodeRunner.Outcome outcome, IReadOnlyList<string> log, IReadOnlyList<string> warnings)
    {
        var detail = new StringBuilder()
            .AppendLine($"Mục đích: {purpose}")
            .AppendLine(scriptId is null ? "Code mới (không thuộc command set)." : $"Script: {scriptId}" + (args is { } a ? $"  args: {a.GetRawText()}" : ""))
            .AppendLine($"Chạy thử (đã huỷ): {changes.Describe()}")
            .AppendLine(outcome.Result is null ? "" : "Kết quả: " + CodeRunner.Truncate(JsonSerializer.Serialize(outcome.Result), MaxPreviewChars))
            .AppendLine(log.Count == 0 ? "" : "Log: " + CodeRunner.Truncate(string.Join(" | ", log), MaxPreviewChars))
            .AppendLine($"──── Code (sha256 {hash[..12]}) ────")
            .Append(code)
            .ToString();
        var reasons = new List<string> { "Code tuỳ ý: đọc kỹ code ở khung dưới trước khi duyệt." };
        if (warnings.Count > 0) reasons.Add("Cảnh báo Revit khi chạy thử: " + string.Join(" | ", warnings.Take(5)));

        var item = new PlanItem
        {
            ItemId = "CODE-1",
            Action = RunCodeAction.Name,
            Params = JsonSerializer.SerializeToElement(new
            {
                summary = $"{purpose} · {changes.Describe()}",
                detail,
                purpose,
                script = scriptId,
                code,
                codeHash = hash,
                args,
                timeoutSeconds,
                historyFile,
            }),
            Tier = PlanTier.Review,
            Reasons = reasons,
            Source = JsonSerializer.SerializeToElement(new { name = scriptId ?? "code", category = "Code" }),
        };
        return host.Plans.Create(
            PlanStorageLocation.For(document),
            kind: RunCodeAction.Name,
            title: $"Chạy code: {purpose}",
            scope: JsonSerializer.SerializeToElement(new { purpose, script = scriptId, codeHash = hash, preview = changes.Summary() }),
            items: [item],
            // Taken after the preview, whose own commit and rollback count as model changes.
            modelStamp: host.Changes.Current(document));
    }

    // What the preview did, by category, from the DocumentChanged events of its transaction.
    private sealed class ChangeCollector
    {
        private readonly Dictionary<string, int> _added = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _modified = new(StringComparer.Ordinal);
        private int _deleted;

        public void OnChanged(object? sender, DocumentChangedEventArgs e)
        {
            var document = e.GetDocument();
            foreach (var id in e.GetAddedElementIds()) Count(_added, document, id);
            foreach (var id in e.GetModifiedElementIds()) Count(_modified, document, id);
            _deleted += e.GetDeletedElementIds().Count;
        }

        public object Summary() => new
        {
            created = _added.Values.Sum(),
            modified = _modified.Values.Sum(),
            deleted = _deleted,
            createdByCategory = Top(_added),
            modifiedByCategory = Top(_modified),
        };

        public string Describe() =>
            $"tạo {_added.Values.Sum()}{List(_added)}, sửa {_modified.Values.Sum()}{List(_modified)}, xoá {_deleted}";

        private static void Count(Dictionary<string, int> counts, Document document, ElementId id)
        {
            var name = document.GetElement(id)?.Category?.Name ?? "(khác)";
            counts[name] = counts.GetValueOrDefault(name) + 1;
        }

        private static Dictionary<string, int> Top(Dictionary<string, int> counts) =>
            counts.OrderByDescending(c => c.Value).Take(MaxListedChanges).ToDictionary(c => c.Key, c => c.Value);

        private static string List(Dictionary<string, int> counts) => counts.Count == 0
            ? ""
            : " (" + string.Join(", ", counts.OrderByDescending(c => c.Value).Take(4).Select(c => $"{c.Key} {c.Value}")) + ")";
    }
}
