using System.Net.Sockets;
using System.Reflection;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Bimtech.RevitMcp.Bridge;
using Bimtech.RevitMcp.Commands;
using Bimtech.RevitMcp.Planning;
using Bimtech.RevitMcp.Settings;
using Bimtech.RevitMcp.Ui;

namespace Bimtech.RevitMcp;

// Revit calls this application when loading/unloading the add-in.
public class App : IExternalApplication
{
    internal const string Version = "0.5.0";
    internal static TcpBridge? Bridge { get; private set; }
    // Shared by every bridge session; plans themselves live in JSON files.
    internal static PlanService Plans { get; } = new(new JsonPlanStore());
    // Created in a Revit API context the first time it is needed; outlives the bridge,
    // so an open review window keeps working while the bridge is stopped and restarted.
    internal static ReviewWindowHost? Review { get; private set; }
    // Counts model changes from Revit start, so plans proposed later can be checked for staleness.
    internal static ModelChangeTracker? Changes { get; private set; }

    private static PushButton? _bridgeButton;
    private static PushButton? _settingsButton;

    public Result OnStartup(UIControlledApplication application)
    {
        Changes = new ModelChangeTracker(application.ControlledApplication);
        SettingsStore.Load();

        var panel = application.CreateRibbonPanel("BIMTECH MCP");
        var assembly = Assembly.GetExecutingAssembly().Location;
        _bridgeButton = panel.AddItem(new PushButtonData(
            "BimtechMcpToggle", "MCP Bridge", assembly, typeof(ToggleBridgeCommand).FullName!)) as PushButton;
        _settingsButton = panel.AddItem(new PushButtonData(
            "BimtechMcpSettings", "Settings", assembly, typeof(OpenSettingsCommand).FullName!)) as PushButton;

        SettingsStore.Changed += _ => RefreshRibbon();

        // ExternalEvent.Create is allowed in OnStartup, so the bridge can start without a click.
        if (SettingsStore.Current.AutoStartBridge) TryStartBridge(out _);
        RefreshRibbon();
        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        StopBridge();
        Review?.Dispose();
        Review = null;
        Changes?.Dispose();
        Changes = null;
        return Result.Succeeded;
    }

    // The commands the bridge serves and the settings window lists. Must run in a Revit API context,
    // because the review window host creates an ExternalEvent.
    internal static CommandRegistry CreateRegistry()
    {
        Review ??= new ReviewWindowHost(Plans);
        return CommandRegistry.CreateDefault(Plans, Review, Changes!);
    }

    // Must run in a Revit API context (OnStartup or an IExternalCommand).
    internal static bool TryStartBridge(out string? error)
    {
        error = null;
        if (Bridge is not null) return true;

        var port = SettingsStore.Current.Port;
        var handler = new RevitRequestHandler(CreateRegistry());
        try
        {
            Bridge = new TcpBridge(port, handler);
        }
        catch (SocketException exception)
        {
            handler.Dispose();
            error = $"Không mở được cổng {port}: {exception.Message}. Đổi cổng trong Settings hoặc tắt chương trình đang dùng cổng này.";
        }
        RefreshRibbon();
        return Bridge is not null;
    }

    internal static void StopBridge()
    {
        Bridge?.Dispose();
        Bridge = null;
        RefreshRibbon();
    }

    // The ribbon always shows whether the bridge runs and whether Claude may change the model.
    internal static void RefreshRibbon()
    {
        var settings = SettingsStore.Current;
        if (_bridgeButton is not null)
        {
            _bridgeButton.ItemText = Bridge is null ? "MCP Bridge\nĐã dừng" : "MCP Bridge\nĐang chạy";
            _bridgeButton.ToolTip = Bridge is null
                ? "Bấm để bật cầu nối cho Claude."
                : $"Đang nhận lệnh tại 127.0.0.1:{settings.Port}. Bấm để tắt.";
        }
        if (_settingsButton is not null)
        {
            _settingsButton.ItemText = settings.ReadOnlyMode ? "Settings\nChỉ đọc" : "Settings\nCho phép ghi";
            _settingsButton.ToolTip = settings.ReadOnlyMode
                ? "Chế độ chỉ đọc: Claude không thể sửa model. Bấm để đổi cài đặt."
                : "Claude được phép sửa model với các lệnh đang bật. Bấm để đổi cài đặt.";
        }
    }
}

// The ribbon button runs in a valid Revit API context.
[Transaction(TransactionMode.Manual)]
public class ToggleBridgeCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        if (App.Bridge is not null)
        {
            App.StopBridge();
            return Result.Succeeded;
        }
        if (App.TryStartBridge(out var error)) return Result.Succeeded;
        message = error ?? "Không bật được cầu nối.";
        return Result.Failed;
    }
}

[Transaction(TransactionMode.Manual)]
public class OpenSettingsCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var before = SettingsStore.Current;
        var window = new SettingsWindow(App.CreateRegistry(), App.Bridge is not null);
        _ = new System.Windows.Interop.WindowInteropHelper(window) { Owner = commandData.Application.MainWindowHandle };
        if (window.ShowDialog() != true) return Result.Cancelled;

        // A new port only takes effect when the bridge reopens its listener.
        if (App.Bridge is not null && before.Port != SettingsStore.Current.Port)
        {
            App.StopBridge();
            if (!App.TryStartBridge(out var error))
                TaskDialog.Show("BIMTECH MCP", error ?? "Không bật lại được cầu nối với cổng mới.");
        }
        return Result.Succeeded;
    }
}
