using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using Bimtech.RevitMcp.Planning;

namespace Bimtech.RevitMcp.Ui;

// Thin code-behind: buttons call the view model; selecting rows asks Revit to show the elements,
// or to draw where the new ones will be.
public partial class ReviewWindow : Window
{
    private readonly ReviewViewModel _model;
    private readonly Action<IReadOnlyList<string>, IReadOnlyList<PreviewGroup>> _focus;

    public ReviewWindow(ReviewViewModel model, Action<IReadOnlyList<string>, IReadOnlyList<PreviewGroup>> focus)
    {
        InitializeComponent();
        DataContext = _model = model;
        _focus = focus;
        // One list of rows, three views: what needs a look, and what the system approved or skipped, folded away.
        var main = new ListCollectionView(model.Items) { Filter = row => ReviewViewModel.IsInMainList((PlanItemRow)row) };
        var approved = new ListCollectionView(model.Items) { Filter = row => ReviewViewModel.IsAutoApproved((PlanItemRow)row) };
        var skipped = new ListCollectionView(model.Items) { Filter = row => ReviewViewModel.IsAutoSkipped((PlanItemRow)row) };
        ItemsGrid.ItemsSource = main;
        ApprovedGrid.ItemsSource = approved;
        AutoGrid.ItemsSource = skipped;
        model.PropertyChanged += (_, e) =>
        {
            // A new plan or a reviewer's change (empty name) can move rows between the two lists.
            if (e.PropertyName is not ("" or null)) return;
            main.Refresh();
            approved.Refresh();
            skipped.Refresh();
        };
        SourceInitialized += (_, _) => UseDarkTitleBar();
    }

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PlanItemRow row) _model.Toggle(row);
    }

    private void ApproveAll_Click(object sender, RoutedEventArgs e) => _model.ApproveAll();

    private void RejectAll_Click(object sender, RoutedEventArgs e) => _model.RejectAll();

    private void Restore_Click(object sender, RoutedEventArgs e) => _model.RestoreChoices();

    private void ApprovePlan_Click(object sender, RoutedEventArgs e) => _model.ApprovePlan();

    private void CancelPlan_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this, "Huỷ kế hoạch này? Mô hình không thay đổi.", Title,
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes) _model.CancelPlan();
    }

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Also called when nothing is selected, so the previous preview is cleared.
        var rows = ((DataGrid)sender).SelectedItems.Cast<PlanItemRow>().ToList();
        var ids = rows.SelectMany(row => row.ElementIds).Distinct().ToList();
        var previews = rows.Select(row => row.Preview).Where(preview => preview.Shapes.Count > 0).ToList();
        _focus(ids, previews);
        ShowDetail(rows.Count == 1 ? rows[0] : null);
    }

    // The selected row's reasons, and its detail (e.g. the code of a send_code item) when it has one.
    private void ShowDetail(PlanItemRow? row)
    {
        DetailCard.Visibility = row is null || (row.Reasons.Length == 0 && row.Detail.Length == 0) ? Visibility.Collapsed : Visibility.Visible;
        DetailReasons.Text = row?.Reasons ?? "";
        DetailReasons.Visibility = string.IsNullOrEmpty(row?.Reasons) ? Visibility.Collapsed : Visibility.Visible;
        DetailText.Text = row?.Detail ?? "";
        DetailText.Visibility = string.IsNullOrEmpty(row?.Detail) ? Visibility.Collapsed : Visibility.Visible;
    }

    // Windows 10 1809+ draws the title bar dark on request; older systems ignore it.
    private void UseDarkTitleBar()
    {
        var dark = 1;
        _ = DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
