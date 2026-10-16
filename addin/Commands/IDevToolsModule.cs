using Bimtech.RevitMcp.Planning;
using Bimtech.RevitMcp.Planning.Actions;
using Bimtech.RevitMcp.Ui;

namespace Bimtech.RevitMcp.Commands;

// Implemented once in Bimtech.RevitMcp.DevTools.dll (devtools/). The add-in creates it only when that DLL
// sits next to the add-in, so the developer tools never reach users who do not have the file.
public interface IDevToolsModule
{
    IEnumerable<IRevitCommand> CreateCommands(DevToolsHost host);

    // Plan actions only the developer tools write, e.g. "run_code" for send_code with write=true.
    IEnumerable<IPlanAction> CreateActions();
}

// The add-in services a developer command may use to propose a plan and open the review window.
public sealed record DevToolsHost(PlanService Plans, ReviewWindowHost Review, ModelChangeTracker Changes);
