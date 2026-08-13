using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using ReactiveUI;
using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Manager;

namespace ServiceLib.ViewModels;

public partial class ExploreSourceItem : MyReactiveObject
{
    [Reactive] public string Name { get; set; } = string.Empty;
    [Reactive] public bool Enabled { get; set; } = true;
    // google / duckduckgo / share_site
    [Reactive] public string Type { get; set; } = "share_site";
    [Reactive] public string Url { get; set; } = string.Empty;
}

public partial class ExploreViewModel : MyReactiveObject
{
    private Process? _proc;

    [Reactive] public ObservableCollection<ExploreSourceItem> Sources { get; set; } = new();
    [Reactive] public string ProxyUrl { get; set; } = string.Empty;
    [Reactive] public int KeyCount { get; set; }
    [Reactive] public bool IsExploring { get; set; }
    [Reactive] public string ProgressText { get; set; } = string.Empty;
    [Reactive] public ObservableCollection<string> Results { get; set; } = new();
    [Reactive] public ObservableCollection<string> SelectedResults { get; set; } = new();

    public ReactiveCommand<RxVoid, RxVoid> StartExploreCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> StopExploreCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> ImportAllCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> ImportSelectedCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> AddShareSiteCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> RemoveShareSiteCmd { get; }

    public EventChannel<RxVoid> RefreshServersRequested { get; } = new();

    public ExploreViewModel()
    {
        // 默认探索插件（类似 qBittorrent 搜索插件，每个一个独立 python）
        Sources.Add(new ExploreSourceItem
        {
            Name = "BluesYoung (gist)",
            Type = "gist",
            Url = "https://gist.github.com/BluesYoung-web",
        });
        Sources.Add(new ExploreSourceItem
        {
            Name = "Hiddify 免费节点",
            Type = "hiddify",
            Url = "https://hiddify.me/docs/Tutorial/hiddify-next-free-node-sharing/",
        });
        Sources.Add(new ExploreSourceItem
        {
            Name = "ClashGithub (freenode)",
            Type = "clashgithub",
            Url = "https://clashgithub.com/category/freenode",
        });
        Sources.Add(new ExploreSourceItem
        {
            Name = "Google 搜索 (按节点 key)",
            Type = "google",
        });

        // 默认代理：本机 IP + 本地 socks 端口
        try
        {
            ProxyUrl = $"{Global.HttpProtocol}{Global.Loopback}:{AppManager.Instance.GetLocalPort(EInboundProtocol.socks)}";
        }
        catch
        {
            ProxyUrl = string.Empty;
        }

        StartExploreCmd = ReactiveCommand.CreateFromTask(StartExploreAsync);
        StopExploreCmd = ReactiveCommand.Create(StopExplore);
        ImportAllCmd = ReactiveCommand.CreateFromTask(ImportAsync);
        ImportSelectedCmd = ReactiveCommand.CreateFromTask(ImportSelectedAsync);
        AddShareSiteCmd = ReactiveCommand.Create(AddShareSite);
        RemoveShareSiteCmd = ReactiveCommand.Create(RemoveShareSite);

        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "v2rayn_diag");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "explore.log"),
                $"{DateTime.Now:HH:mm:ss} Ctor: Sources={Sources.Count} names=[{string.Join(",", Sources.Select(s => s.Name))}]\n");
        }
        catch { }
    }

    #region 源管理

    private void AddShareSite()
    {
        Sources.Add(new ExploreSourceItem { Name = "new site", Type = "share_site", Enabled = true });
    }

    private void RemoveShareSite()
    {
        if (SelectedShareSite is { } item)
        {
            Sources.Remove(item);
        }
    }

    [Reactive] public ExploreSourceItem? SelectedShareSite { get; set; }

    #endregion 源管理

    #region 探索

    private async Task StartExploreAsync()
    {
        if (IsExploring)
        {
            return;
        }

        var keys = await CollectSeedKeys();
        KeyCount = keys.Count;
        if (keys.Count == 0)
        {
            NoticeManager.Instance.Enqueue(ResUI.ExploreNoNodes);
            return;
        }

        var scriptPath = Path.Combine(AppContext.BaseDirectory, "explore_nodes.py");
        if (!File.Exists(scriptPath))
        {
            NoticeManager.Instance.Enqueue($"{ResUI.menuExploreNodes}: explore_nodes.py not found at {scriptPath}");
            return;
        }

        var tmpJson = Path.Combine(Path.GetTempPath(), $"v2rayn_explore_{Guid.NewGuid():N}.json");
        var tmpOut = Path.Combine(Path.GetTempPath(), $"v2rayn_explore_out_{Guid.NewGuid():N}.txt");
        var tmpSrc = Path.Combine(Path.GetTempPath(), $"v2rayn_explore_src_{Guid.NewGuid():N}.json");
        File.WriteAllText(tmpJson, JsonUtils.Serialize(keys, false));
        File.WriteAllText(tmpSrc, BuildSourcesConfig());

        Results.Clear();
        ProgressText = $"{ResUI.menuExploreNodes}: {keys.Count} keys, searching...";
        IsExploring = true;

        await Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "python",
                    Arguments = $"\"{scriptPath}\" \"{tmpJson}\" \"{tmpOut}\" \"{ProxyUrl}\" \"{tmpSrc}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var proc = Process.Start(psi);
                _proc = proc;
                if (proc is null)
                {
                    throw new Exception("failed to start python");
                }

                // 进度: 读 stderr
                var errTask = Task.Run(() =>
                {
                    var line = proc.StandardError.ReadLine();
                    while (line is not null)
                    {
                        AppendProgress(line);
                        line = proc.StandardError.ReadLine();
                    }
                });

                // 结果: 轮询 out.txt, 把新行加进 Results
                var lastPos = 0L;
                while (!proc.HasExited)
                {
                    if (File.Exists(tmpOut))
                    {
                        using var fs = new FileStream(tmpOut, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        if (fs.Length > lastPos)
                        {
                            fs.Seek(lastPos, SeekOrigin.Begin);
                            using var sr = new StreamReader(fs);
                            var rest = sr.ReadToEnd();
                            lastPos = fs.Position;
                            AppendResults(rest);
                        }
                    }
                    Thread.Sleep(500);
                }
                // 收尾再读一次
                if (File.Exists(tmpOut))
                {
                    AppendResults(File.ReadAllText(tmpOut).Substring((int)Math.Min(lastPos, int.MaxValue)));
                }
                errTask.Wait();
            }
            catch (Exception ex)
            {
                AppendProgress($"ERROR: {ex.Message}");
            }
            finally
            {
                IsExploring = false;
                try { File.Delete(tmpJson); File.Delete(tmpOut); File.Delete(tmpSrc); } catch { }
            }
        });
    }

    private void StopExplore()
    {
        try { _proc?.Kill(); } catch { }
        IsExploring = false;
        AppendProgress(ResUI.OperationFailed);
    }

    private void AppendProgress(string line)
    {
        var text = ProgressText + Environment.NewLine + line;
        if (text.Length > 4000) text = text[^4000..];
        ProgressText = text;
    }

    private void AppendResults(string text)
    {
        foreach (var line in text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith("#")) continue;
            if (!Results.Contains(t)) Results.Add(t);
        }
    }

    #endregion 探索

    #region 导入

    private async Task ImportAsync()
    {
        await DoImport(Results.ToList());
    }

    private async Task ImportSelectedAsync()
    {
        var sel = SelectedResults.Where(t => !t.IsNullOrEmpty()).ToList();
        if (sel.Count == 0)
        {
            NoticeManager.Instance.Enqueue(ResUI.PleaseSelectServer);
            return;
        }
        await DoImport(sel);
    }

    private async Task DoImport(List<string> lines)
    {
        if (lines is null or { Count: 0 })
        {
            NoticeManager.Instance.Enqueue(ResUI.ExploreNoNodes);
            return;
        }
        var cleaned = string.Join(Environment.NewLine,
            lines.Select(t => t.Trim())
                .Where(t => !t.StartsWith("ssr://", StringComparison.OrdinalIgnoreCase)));
        if (cleaned.IsNullOrEmpty())
        {
            NoticeManager.Instance.Enqueue(ResUI.ExploreNoNodes);
            return;
        }

        var added = await ConfigHandler.AddBatchServers(_config, cleaned, Global.ExploreSubId, false);
        if (added > 0)
        {
            var subs = await AppManager.Instance.SubItems();
            if (subs?.All(t => t.Id != Global.ExploreSubId) == true)
            {
                await ConfigHandler.AddSubItem(_config, new SubItem
                {
                    Id = Global.ExploreSubId,
                    Remarks = ResUI.menuExploreNodes,
                });
            }
            NoticeManager.Instance.SendMessage($"{ResUI.menuExploreNodes}: +{added}");
            RefreshServersRequested.Publish();
        }
        else
        {
            NoticeManager.Instance.Enqueue(ResUI.OperationFailed);
        }
    }

    #endregion 导入

    #region 辅助

    private string BuildSourcesConfig()
    {
        bool Enabled(string type) =>
            Sources.FirstOrDefault(t => t.Type == type)?.Enabled ?? false;
        var sites = Sources.Where(t => t.Type == "share_site" && t.Enabled && !t.Url.IsNullOrEmpty())
            .Select(t => t.Url)
            .ToList();
        var cfg = new Dictionary<string, object>
        {
            ["gist"] = Enabled("gist"),
            ["hiddify"] = Enabled("hiddify"),
            ["clashgithub"] = Enabled("clashgithub"),
            ["google"] = Enabled("google"),
            ["share_sites"] = sites,
        };
        return JsonUtils.Serialize(cfg, false);
    }

    private async Task<List<string>> CollectSeedKeys()
    {
        var all = await AppManager.Instance.ProfileItems(string.Empty);
        if (all is null) return new List<string>();

        var subItems = await AppManager.Instance.SubItems();
        var locked = (subItems ?? []).Where(t => t.LockGroupNodes).Select(t => t.Id).ToHashSet();

        return all
            .Where(t => t.Subid != Global.RecycleBinSubId && !locked.Contains(t.Subid))
            .Where(t => t.ConfigType is EConfigType.Hysteria2 or EConfigType.VMess or EConfigType.VLESS)
            .Select(t => t.Password)
            .Where(p => !p.IsNullOrEmpty())
            .Distinct()
            .ToList();
    }

    #endregion 辅助
}
