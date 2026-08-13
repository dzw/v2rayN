using System.Windows.Controls;

namespace v2rayN.Base;

public static class ViewHost
{
    public static void Show(
        ContentControl host,
        object? viewModel)
    {
        if (viewModel == null)
        {
            host.Content = null;
            return;
        }

        var view = SimpleViewLocator.Instance.ResolveView(viewModel);
        view!.ViewModel = viewModel;

        // WPF 的 ReactiveUserControl<T> 不会自动把 ViewModel 同步到 DataContext，
        // 而 XAML 里的 {Binding ...} 默认绑定 DataContext，必须显式关联。
        if (view is System.Windows.FrameworkElement fe)
        {
            fe.DataContext = viewModel;
        }

        host.Content = view;

        if (viewModel is ServiceLib.ViewModels.ExploreViewModel evm)
        {
            try
            {
                var dir = Path.Combine(Path.GetTempPath(), "v2rayn_diag");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "explore.log"),
                    $"{DateTime.Now:HH:mm:ss} ViewHost.Show Explore: viewType={view?.GetType().Name} hostContent={host.Content?.GetType().Name} hostDC={host.DataContext?.GetType().Name} sources={evm.Sources.Count}\n");
            }
            catch { }
        }
    }
}
