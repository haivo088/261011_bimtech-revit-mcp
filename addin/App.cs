using System.Reflection;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Bimtech.RevitMcp.Bridge;
using Bimtech.RevitMcp.Commands;

namespace Bimtech.RevitMcp;

// Revit calls this application when loading/unloading the add-in.
public class App : IExternalApplication
{
    internal const int BridgePort = 8081;
    internal const string Version = "0.3.0";
    internal static TcpBridge? Bridge { get; set; }

    public Result OnStartup(UIControlledApplication application)
    {
        var panel = application.CreateRibbonPanel("BIMTECH MCP");
        panel.AddItem(new PushButtonData(
            "BimtechMcpToggle", "Start / Stop\nMCP Bridge",
            Assembly.GetExecutingAssembly().Location,
            typeof(ToggleBridgeCommand).FullName!));
        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        Bridge?.Dispose();
        Bridge = null;
        return Result.Succeeded;
    }
}

// The ribbon button runs in a valid Revit API context.
[Transaction(TransactionMode.Manual)]
public class ToggleBridgeCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            if (App.Bridge is not null)
            {
                App.Bridge.Dispose();
                App.Bridge = null;
                TaskDialog.Show("BIMTECH MCP", "Bridge stopped.");
            }
            else
            {
                var handler = new RevitRequestHandler(CommandRegistry.CreateDefault());
                try { App.Bridge = new TcpBridge(App.BridgePort, handler); }
                catch { handler.Dispose(); throw; }
                TaskDialog.Show("BIMTECH MCP", $"Bridge listening at 127.0.0.1:{App.BridgePort}.");
            }
            return Result.Succeeded;
        }
        catch (Exception exception)
        {
            message = exception.Message;
            return Result.Failed;
        }
    }
}
