using System.Windows;
using System.Windows.Controls;
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
        // Also called when nothing is selected, so the previous preview is cleared.
        var rows = ((DataGrid)sender).SelectedItems.Cast<PlanItemRow>().ToList();
        var ids = rows.SelectMany(row => row.ElementIds).Distinct().ToList();
        var previews = rows.Select(row => row.Preview).Where(preview => preview.Shapes.Count > 0).ToList();
        _focus(ids, previews);
    }
}
