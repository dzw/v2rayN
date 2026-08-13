using Application = Avalonia.Application;
using ReactiveUI;
using System.Reactive.Linq;

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
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(async text =>
                {
                    if (Application.Current?.Clipboard is { } clipboard)
                    {
                        await clipboard.SetTextAsync(text);
                    }
                })
                .DisposeWith(disposables);
        });
    }
}
