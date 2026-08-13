using System.Windows.Controls;
using ReactiveUI;
using ServiceLib.ViewModels;

namespace v2rayN.Views;

public partial class ExploreView : ReactiveUserControl<ExploreViewModel>
{
    public ExploreView()
    {
        InitializeComponent();

        lstResults.SelectionChanged += LstResults_SelectionChanged;
    }

    private void LstResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is null) return;
        ViewModel.SelectedResults.Clear();
        foreach (var item in lstResults.SelectedItems)
        {
            if (item is string s) ViewModel.SelectedResults.Add(s);
        }
    }
}
