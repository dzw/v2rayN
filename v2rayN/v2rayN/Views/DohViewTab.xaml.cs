using System.Windows.Controls;
using ReactiveUI;
using ServiceLib.ViewModels;
using WindowsUtils = v2rayN.Common.WindowsUtils;

namespace v2rayN.Views;

public partial class DohViewTab
{
    public DohViewTab()
    {
        InitializeComponent();

        this.WhenActivated(disposables =>
        {
            this.BindCommand(ViewModel, vm => vm.QueryCmd, v => v.txtDomain).DisposeWith(disposables);

            ViewModel.CopyRequested
                .AsObservable()
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(text => WindowsUtils.SetClipboardData(text))
                .DisposeWith(disposables);
        });
    }

    private void LstResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }
        var selected = ((DataGrid)sender).SelectedItems;
        ViewModel.SelectedResults.Clear();
        foreach (DohResultItem item in selected)
        {
            ViewModel.SelectedResults.Add(item);
        }
    }
}
