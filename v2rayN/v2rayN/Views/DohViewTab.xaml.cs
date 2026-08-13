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
            this.BindCommand(ViewModel, vm => vm.QueryCmd, v => v.txtDohUrl).DisposeWith(disposables);

            ViewModel.CopyRequested
                .AsObservable()
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(text => WindowsUtils.SetClipboardData(text))
                .DisposeWith(disposables);
        });
    }
}
