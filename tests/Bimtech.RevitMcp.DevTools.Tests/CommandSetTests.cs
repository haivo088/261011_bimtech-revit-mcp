using System.Text.Json;
using Bimtech.RevitMcp.DevTools;
using Xunit;
using static Bimtech.RevitMcp.DevTools.CommandSetStore;

namespace Bimtech.RevitMcp.DevTools.Tests;

public sealed class CommandSetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bimtech-commandsets-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static Script Rename() => new("family-tcvn", "rename-params", "Đổi tên tham số theo TCVN", true,
        [new Param("prefix", "string", "Tiền tố", J("\"BTC_\"")), new Param("ids", "elementId[]", Required: true), new Param("dryRun", "boolean")],
        ["Dùng cho family cột 2026-10"]);

    [Fact]
    public void A_saved_script_is_listed_and_loaded_with_its_parameters()
    {
        var store = new CommandSetStore(_root);
        store.Save(Rename(), "return ctx.Str(\"prefix\");", "Chuẩn hoá family theo TCVN", overwrite: false);

        var set = Assert.Single(store.List());
        Assert.Equal("Chuẩn hoá family theo TCVN", set.Description);
        var script = Assert.Single(set.Scripts);
        Assert.Equal("family-tcvn/rename-params", script.Id);
        Assert.True(script.Write);
        Assert.Equal(["prefix", "ids", "dryRun"], script.Params.Select(p => p.Name));
        Assert.True(script.Params[1].Required);
        Assert.Equal("\"BTC_\"", script.Params[0].Default?.GetRawText());
        var (info, code) = store.Load("family-tcvn/rename-params");
        Assert.Equal("return ctx.Str(\"prefix\");\n", code);
        Assert.Equal(["Dùng cho family cột 2026-10"], info.Notes);
    }

    [Fact]
    public void Saving_over_an_existing_script_needs_overwrite()
    {
        var store = new CommandSetStore(_root);
        store.Save(Rename(), "return 1;", null, overwrite: false);

        Assert.Throws<CommandSetException>(() => store.Save(Rename(), "return 2;", null, overwrite: false));
        store.Save(Rename(), "return 2;", null, overwrite: true);
        Assert.Equal("return 2;\n", store.Load("family-tcvn/rename-params").Code);
    }

    [Fact]
    public void Bad_names_and_parameter_types_are_refused()
    {
        var store = new CommandSetStore(_root);

        Assert.Throws<CommandSetException>(() => store.Save(Rename() with { Name = "Rename Params" }, "return 1;", null, false));
        Assert.Throws<CommandSetException>(() => store.Save(Rename() with { Params = [new Param("x", "date")] }, "return 1;", null, false));
        Assert.Throws<CommandSetException>(() => store.Save(Rename() with { Params = [new Param("x", "number", Default: J("\"a\""))] }, "return 1;", null, false));
        Assert.Throws<CommandSetException>(() => store.Load("../etc"));
        Assert.Throws<CommandSetException>(() => store.Load("family-tcvn/missing"));
    }

    [Fact]
    public void Arguments_are_checked_and_defaults_filled_in()
    {
        var bound = BindArgs(Rename(), J("""{ "ids": ["12345", 678] }"""));

        Assert.Equal("BTC_", bound.GetProperty("prefix").GetString());
        Assert.Equal(2, bound.GetProperty("ids").GetArrayLength());
        Assert.False(bound.TryGetProperty("dryRun", out _));
    }

    [Fact]
    public void Missing_wrong_or_unknown_arguments_are_refused()
    {
        Assert.Throws<CommandSetException>(() => BindArgs(Rename(), J("{}")));
        Assert.Throws<CommandSetException>(() => BindArgs(Rename(), J("""{ "ids": ["abc"] }""")));
        Assert.Throws<CommandSetException>(() => BindArgs(Rename(), J("""{ "ids": [1], "prefx": "X" }""")));
        Assert.Throws<CommandSetException>(() => BindArgs(Rename(), J("""{ "ids": [1], "dryRun": "yes" }""")));
    }

    [Fact]
    public void History_returns_the_latest_runs_first_and_skips_broken_lines()
    {
        var file = Path.Combine(_root, "logs", "code-runs.jsonl");
        for (var i = 0; i < 3; i++)
            CodeHistory.Append(file, new CodeHistory.Entry(DateTimeOffset.Now, "read", $"việc {i}", null, false, true, "done", "h" + i, "return 1;", "TEST"));
        File.AppendAllText(file, "not json\n");

        var entries = CodeHistory.Read(file, 2);

        Assert.Equal(["việc 2", "việc 1"], entries.Select(e => e.Purpose));
    }

    [Fact]
    public void Long_code_is_cut_in_the_history()
    {
        var file = Path.Combine(_root, "history.jsonl");
        CodeHistory.Append(file, new CodeHistory.Entry(DateTimeOffset.Now, "read", null, null, false, true, "done", "h",
            new string('x', CodeHistory.MaxCodeChars + 100), null));

        Assert.Equal(CodeHistory.MaxCodeChars + 1, CodeHistory.Read(file, 1)[0].Code!.Length);
    }
}
