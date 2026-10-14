using System.Windows;
using System.Windows.Controls;
using Bimtech.RevitMcp.Planning;

namespace Bimtech.RevitMcp.Ui;

// Thin code-behind: buttons call the view model; selecting rows asks Revit to show the elements.
public partial class ReviewWindow : Window
{
    private readonly ReviewViewModel _model;
    private readonly Action<IReadOnlyList<string>> _focus;

    public ReviewWindow(ReviewViewModel model, Action<IReadOnlyList<string>> focus)
    {
        InitializeComponent();
        DataContext = _model = model;
        _focus = focus;
    }

    private List<PlanItemRow> SelectedRows => ItemsGrid.SelectedItems.Cast<PlanItemRow>().ToList();

    private void Approve_Click(object sender, RoutedEventArgs e) => _model.SetStatus(SelectedRows, PlanItemStatus.Approved);

    private void Reject_Click(object sender, RoutedEventArgs e) => _model.SetStatus(SelectedRows, PlanItemStatus.Rejected);

    private void Reset_Click(object sender, RoutedEventArgs e) => _model.SetStatus(SelectedRows, PlanItemStatus.Pending);

    private void RejectRemaining_Click(object sender, RoutedEventArgs e) => _model.RejectRemaining();

    private void ApprovePlan_Click(object sender, RoutedEventArgs e) => _model.ApprovePlan();

    private void CancelPlan_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this, "Huỷ kế hoạch này? Mô hình không thay đổi.", Title,
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes) _model.CancelPlan();
    }

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var ids = ((DataGrid)sender).SelectedItems.Cast<PlanItemRow>()
            .SelectMany(row => row.ElementIds).Distinct().ToList();
        if (ids.Count > 0) _focus(ids);
    }
}
