namespace Bimtech.RevitMcp.Commands;

// Implemented once in Bimtech.RevitMcp.DevTools.dll (devtools/). The add-in creates it only when that DLL
// sits next to the add-in, so the developer tools never reach users who do not have the file.
public interface IDevToolsModule
{
    IEnumerable<IRevitCommand> CreateCommands();
}
