using ReactiveUI;
using v2rayN.Desktop.Common;

namespace v2rayN.Desktop.Views;

public partial class DohViewTab : ReactiveUserControl<DohViewModel>
{
    public DohViewTab()
    {
        InitializeComponent();

        this.WhenActivated(disposables =>
        {
            ViewModel!.CopyRequested
                .AsObservable()
                .Subscribe(async text => await AvaUtils.SetClipboardData(this, text))
                .DisposeWith(disposables);
        });
    }
}
