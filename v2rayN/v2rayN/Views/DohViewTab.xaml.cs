using System.Windows.Controls;
using ReactiveUI;
using ServiceLib.Handler;
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

    private void OpenHosts_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        const string hostsPath = @"C:\Windows\System32\drivers\etc\hosts";
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "notepad.exe",
                Arguments = hostsPath,
                Verb = "runas", // 以管理员方式运行
                UseShellExecute = true,
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // 用户取消 UAC 提权
            NoticeManager.Instance.SendMessage($"打开 Hosts 失败: {ex.Message}");
        }
    }
}
