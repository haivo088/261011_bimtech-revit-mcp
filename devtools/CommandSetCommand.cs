using System.Text.Json;
using Bimtech.RevitMcp.Commands;
using Bimtech.RevitMcp.Protocol;

namespace Bimtech.RevitMcp.DevTools;

// The command sets in the repository and the send_code history:
//   action "list":    every set and script with its description, write flag and parameters;
//   action "save":    keeps a snippet that worked as set/script with typed parameters (it must compile; the user agreed);
//   action "history": the latest send_code runs, newest first, to spot jobs that keep coming back.
// The server passes commandSetRoot and historyFile. Nothing here touches the model.
public sealed class CommandSetCommand : IRevitCommand
{
    public string Method => "command_set";
    public CommandAccess Access => CommandAccess.Read;

    public object? Execute(CommandContext context)
    {
        var p = context.Params;
        var root = p.GetString("commandSetRoot", 1000) ?? throw RpcException.InvalidParams("commandSetRoot", "the server sent no folder.");
        var store = new CommandSetStore(root);
        try
        {
            return p.RequireString("action", 20) switch
            {
                "list" => new { root, sets = store.List().Select(set => new
                {
                    set.Name,
                    set.Description,
                    scripts = set.Scripts.Select(script => new
                    {
                        id = script.Id, script.Description, script.Write, script.Params, script.Notes,
                    }),
                }) },
                "save" => Save(p, store),
                "history" => new
                {
                    entries = CodeHistory.Read(p.GetString("historyFile", 1000), p.GetInt("count", 30, 1, 500)),
                    note = "Look for the same kind of job run several times; propose saving it as a command set.",
                },
                _ => throw RpcException.InvalidParams("action", "expected \"list\", \"save\" or \"history\"."),
            };
        }
        catch (CommandSetException exception)
        {
            throw RpcException.InvalidParams("script", exception.Message);
        }
    }

    private static object Save(CommandParams p, CommandSetStore store)
    {
        var id = p.RequireString("script", 120).Split('/');
        if (id.Length != 2) throw RpcException.InvalidParams("script", "expected \"set/script\".");
        var code = p.RequireString("code", 100_000);
        var parameters = p.GetObjectList("params", 40).Select(param => new CommandSetStore.Param(
            param.RequireString("name", 40), param.RequireString("type", 20), param.GetString("description", 500),
            param.GetJsonValue("default"), param.GetBool("required", false))).ToList();
        var script = new CommandSetStore.Script(id[0], id[1], p.RequireString("description", 500), p.GetBool("write", false),
            parameters, p.GetStringList("notes", 50, 500));
        CommandSetStore.ValidateParams(parameters);

        // Saved only when it compiles, so a script never fails for a typo the first time someone runs it.
        var compiled = QueryCompiler.Compile(code);
        if (compiled.Run is null)
            return new { saved = false, stage = "compile", errors = compiled.Errors };
        var saved = store.Save(script, code, p.GetString("setDescription", 500), p.GetBool("overwrite", false));
        return new
        {
            saved = true,
            id = saved.Id,
            files = new[] { $"{saved.Set}/{saved.Name}.cs", $"{saved.Set}/{saved.Name}.json" },
            note = "Saved in the repository's commandsets folder. Run it with send_code script=\"" + saved.Id + "\"; "
                + "commit the files with git when the user is happy with it.",
        };
    }
}
