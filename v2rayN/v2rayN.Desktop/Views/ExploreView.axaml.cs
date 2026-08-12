using Avalonia.VisualTree;
using ReactiveUI;

namespace v2rayN.Desktop.Views;

public partial class ExploreView : ReactiveUserControl<ExploreViewModel>
{
    public ExploreView()
    {
        InitializeComponent();

        lstResults.SelectionChanged += (_, _) =>
        {
            if (ViewModel is null) return;
            ViewModel.SelectedResults.Clear();
            foreach (var item in lstResults.SelectedItems)
            {
                if (item is string s) ViewModel.SelectedResults.Add(s);
            }
        };
    }
}
