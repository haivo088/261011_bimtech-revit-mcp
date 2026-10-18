using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using Bimtech.RevitMcp.Commands;

namespace Bimtech.RevitMcp.Settings;

// Edits a draft of the settings; nothing changes until "Lưu" writes settings.json through SettingsStore.
public partial class SettingsWindow : Window
{
    private static readonly Dictionary<string, string> GroupLabels = new()
    {
        ["Diagnostics"] = "Chẩn đoán",
        ["Documents"] = "Thông tin model",
        ["Elements"] = "Phần tử",
        ["Structural"] = "Kết cấu từ CAD",
        ["Parameters"] = "Đề xuất (chờ duyệt)",
        ["Plans"] = "Kế hoạch: duyệt và ghi",
    };

    private readonly List<CommandRow> _rows;

    public SettingsWindow(CommandRegistry registry, bool bridgeRunning)
    {
        InitializeComponent();
        var settings = SettingsStore.Current;

        StatusText.Text = bridgeRunning
            ? $"Cầu nối đang chạy tại 127.0.0.1:{settings.Port} - phiên bản {App.Version}"
            : $"Cầu nối đang tắt - phiên bản {App.Version}";
        if (SettingsStore.LoadProblem is { } problem)
        {
            ProblemText.Text = problem;
            ProblemBanner.Visibility = Visibility.Visible;
        }

        ReadOnlyCheck.IsChecked = settings.ReadOnlyMode;
        PortBox.Text = settings.Port.ToString();
        MaxElementsBox.Text = settings.MaxElementsPerWrite.ToString();
        AutoStartCheck.IsChecked = settings.AutoStartBridge;
        AutoCommitCheck.IsChecked = settings.AutoCommitSure;
        CommitOnApproveCheck.IsChecked = settings.CommitOnApprove;
        FilePathText.Text = "Lưu tại: " + SettingsStore.FilePath;

        _rows = registry.Commands.Select(command => new CommandRow(
            command,
            GroupLabels.GetValueOrDefault(CommandRegistry.GroupOf(command), CommandRegistry.GroupOf(command)),
            CommandPolicy.IsEnabled(command, settings),
            Draft)).ToList();
        var view = CollectionViewSource.GetDefaultView(_rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(CommandRow.Group)));
        CommandGrid.ItemsSource = view;
        RefreshStatuses();
    }

    // The settings as they would be saved now, used to show each command's effective status.
    private McpSettings Draft() => SettingsStore.Current with
    {
        ReadOnlyMode = ReadOnlyCheck.IsChecked == true,
        DisabledCommands = _rows?.Where(row => !row.IsEnabled).Select(row => row.Method).ToList() ?? [],
    };

    private void RefreshStatuses()
    {
        foreach (var row in _rows) row.RefreshStatus();
    }

    private void OnReadOnlyChanged(object sender, RoutedEventArgs e)
    {
        if (_rows is not null) RefreshStatuses();
    }

    private void OnEnableAll(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows) row.IsEnabled = true;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1024 or > 65535)
        {
            ErrorText.Text = "Cổng phải là số từ 1024 đến 65535.";
            PortBox.Focus();
            return;
        }
        if (!int.TryParse(MaxElementsBox.Text.Trim(), out var maxElements)
            || maxElements < 1 || maxElements > McpSettings.MaxElementsPerWriteLimit)
        {
            ErrorText.Text = $"Giới hạn phần tử phải từ 1 đến {McpSettings.MaxElementsPerWriteLimit}.";
            MaxElementsBox.Focus();
            return;
        }

        try
        {
            SettingsStore.Save(Draft() with
            {
                Port = port,
                MaxElementsPerWrite = maxElements,
                AutoStartBridge = AutoStartCheck.IsChecked == true,
                AutoCommitSure = AutoCommitCheck.IsChecked == true,
                CommitOnApprove = CommitOnApproveCheck.IsChecked == true,
            });
        }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException)
        {
            ErrorText.Text = "Không lưu được cài đặt: " + exception.Message;
            return;
        }
        DialogResult = true;
    }

    // One line of the command table.
    private sealed class CommandRow : INotifyPropertyChanged
    {
        private readonly IRevitCommand _command;
        private readonly Func<McpSettings> _draft;
        private bool _isEnabled;
        private string _statusLabel = "";

        public CommandRow(IRevitCommand command, string group, bool isEnabled, Func<McpSettings> draft)
        {
            _command = command;
            _draft = draft;
            _isEnabled = isEnabled;
            Group = group;
        }

        public string Method => _command.Method;
        public string Group { get; }
        public bool CanToggle => !CommandPolicy.AlwaysEnabled.Contains(Method);

        public string AccessLabel => _command.Access switch
        {
            CommandAccess.Write => "Ghi",
            CommandAccess.Delete => "Xóa",
            _ => "Đọc",
        };

        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled == value) return;
                _isEnabled = value;
                OnPropertyChanged();
                RefreshStatus();
            }
        }

        public string StatusLabel
        {
            get => _statusLabel;
            private set
            {
                _statusLabel = value;
                OnPropertyChanged();
            }
        }

        public void RefreshStatus()
        {
            var draft = _draft();
            StatusLabel = !CommandPolicy.IsEnabled(_command, draft) ? "Đã tắt"
                : !draft.ReadOnlyMode || _command.Access == CommandAccess.Read ? "Đang bật"
                : CommandPolicy.IsAvailable(_command, draft) ? "Chỉ xem trước (chỉ đọc)"
                : "Bị chặn (chỉ đọc)";
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
