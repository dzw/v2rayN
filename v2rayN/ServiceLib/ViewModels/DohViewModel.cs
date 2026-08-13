using System.Buffers;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ReactiveUI;
using ServiceLib.Handler;
using ServiceLib.Manager;

namespace ServiceLib.ViewModels;

public partial class DohResultItem : MyReactiveObject
{
    [Reactive] public string Domain { get; set; } = string.Empty;
    [Reactive] public string Server { get; set; } = string.Empty;
    [Reactive] public string Type { get; set; } = string.Empty;
    [Reactive] public string Address { get; set; } = string.Empty;
    [Reactive] public int Ttl { get; set; }
}

public partial class DohViewModel : MyReactiveObject
{
    [Reactive] public string Domains { get; set; } = string.Empty;
    [Reactive] public string NewDohUrl { get; set; } = string.Empty;
    [Reactive] public string ProxyUrl { get; set; } = string.Empty;
    [Reactive] public int Concurrency { get; set; } = 4; // 一个域名同时使用的 DoH 服务器数量

    // 可增删的 DoH 服务器列表（持久化）
    public ObservableCollection<string> DohUrls { get; } = new();

    [Reactive] public int ResolveType { get; set; } = 0; // 0=A, 1=AAAA, 2=Both
    [Reactive] public ObservableCollection<DohResultItem> Results { get; set; } = new();
    // 结果列表中当前选中的行（支持多选复制）
    public System.Collections.Generic.List<DohResultItem> SelectedResults { get; } = new();
    [Reactive] public string ProgressText { get; set; } = string.Empty;
    [Reactive] public bool IsBusy { get; set; }

    public ReactiveCommand<RxVoid, RxVoid> QueryCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> CopyResultsCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> AddDohCmd { get; }
    public ReactiveCommand<string, RxVoid> RemoveDohCmd { get; }

    public EventChannel<string> CopyRequested { get; } = new();

    private static readonly List<string> DefaultDohUrls =
    [
        "https://1.1.1.1/dns-query",
        "https://cloudflare-dns.com/dns-query",
        "https://dns.google/dns-query",
        "https://dns.cloudflare.com/dns-query",
        "https://dns.adguard.com/dns-query",
        "https://dns.quad9.net/dns-query",
        "https://doh.opendns.com/dns-query",
        "https://dnsforge.de/dns-query",
    ];

    public DohViewModel()
    {
        _config = AppManager.Instance.Config;
        _config.SimpleDNSItem ??= new SimpleDNSItem();

        // 读取上次保存的 DoH 查询内容（域名列表与服务器列表）
        if (_config.SimpleDNSItem is not null)
        {
            Domains = _config.SimpleDNSItem.DohDomains ?? string.Empty;
            var saved = _config.SimpleDNSItem.DohUrls;
            if (saved is { Count: > 0 })
            {
                foreach (var u in saved)
                {
                    if (u.IsNotEmpty())
                    {
                        DohUrls.Add(u);
                    }
                }
            }
            else
            {
                foreach (var u in DefaultDohUrls)
                {
                    DohUrls.Add(u);
                }
            }
        }

        QueryCmd = ReactiveCommand.CreateFromTask(async () => await QueryAsync());
        CopyResultsCmd = ReactiveCommand.Create(() =>
        {
            var items = SelectedResults.Count > 0 ? SelectedResults : Results.ToList();
            if (items.Count == 0)
            {
                return;
            }
            var text = string.Join(Environment.NewLine, items.Select(r => $"{r.Address}\t{r.Domain}"));
            CopyRequested.Publish(text);
        });
        AddDohCmd = ReactiveCommand.Create(AddDoh);
        RemoveDohCmd = ReactiveCommand.Create<string>(RemoveDoh);

        // 域名 / 服务器列表变化时写回配置并持久化，保证下次打开仍在
        this.WhenAnyValue(x => x.Domains).Subscribe(_ => SaveDohSettings());
        DohUrls.CollectionChanged += (_, _) => SaveDohSettings();
    }

    private void AddDoh()
    {
        var url = NewDohUrl.Trim();
        if (url.IsNullOrEmpty())
        {
            return;
        }
        if (!DohUrls.Contains(url, StringComparer.OrdinalIgnoreCase))
        {
            DohUrls.Add(url);
        }
        NewDohUrl = string.Empty;
    }

    private void RemoveDoh(string url)
    {
        if (url.IsNotEmpty())
        {
            DohUrls.Remove(url);
        }
    }

    private void SaveDohSettings()
    {
        if (_config?.SimpleDNSItem is null)
        {
            return;
        }
        _config.SimpleDNSItem.DohDomains = Domains;
        _config.SimpleDNSItem.DohUrls = DohUrls.ToList();
        _ = ConfigHandler.SaveConfig(_config);
    }

    private async Task QueryAsync()
    {
        if (IsBusy)
        {
            return;
        }
        Results.Clear();
        ProgressText = string.Empty;

        var domains = Domains.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => d.Trim().TrimEnd('.'))
            .Where(d => d.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (domains.Count == 0)
        {
            ProgressText = "请填写要查询的域名";
            return;
        }

        var servers = DohUrls
            .Select(u => u.Trim())
            .Where(u => u.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (servers.Count == 0)
        {
            ProgressText = "请先添加 DoH 服务器地址";
            return;
        }

        var concurrency = Math.Clamp(Concurrency, 1, servers.Count);

        IsBusy = true;
        try
        {
            var types = ResolveType switch
            {
                1 => new[] { DnsType.AAAA },
                2 => new[] { DnsType.A, DnsType.AAAA },
                _ => new[] { DnsType.A },
            };

            var log = new StringBuilder();
            log.AppendLine($"使用 {servers.Count} 个 DoH 服务器（并发 {concurrency}）查询 {domains.Count} 个域名");

            using var client = CreateClient();
            var options = new ParallelOptions { MaxDegreeOfParallelism = concurrency };

            // 逐个域名处理：每个域名的查询通过最多 a 个 DoH 服务器并发进行
            foreach (var domain in domains)
            {
                log.AppendLine($"---- {domain} ----");
                var localResults = new List<DohResultItem>();
                var localLog = new StringBuilder();
                await Parallel.ForEachAsync(servers, options, async (server, ct) =>
                {
                    var target = BuildDohTarget(server);
                    foreach (var t in types)
                    {
                        try
                        {
                            var answers = await ResolveAsync(client, target, domain, t);
                            lock (localResults)
                            {
                                foreach (var a in answers)
                                {
                                    a.Domain = domain;
                                    a.Server = server;
                                    localResults.Add(a);
                                    localLog.AppendLine($"  [{server}] {a.Type} {a.Address} (ttl={a.Ttl})");
                                }
                                if (answers.Count == 0)
                                {
                                    localLog.AppendLine($"  [{server}] 无记录");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            lock (localResults)
                            {
                                localLog.AppendLine($"  [{server}] 失败: {ex.Message}");
                            }
                        }
                    }
                });

                lock (Results)
                {
                    foreach (var r in localResults)
                    {
                        Results.Add(r);
                    }
                }
                log.Append(localLog);
            }
            log.AppendLine("完成");
            ProgressText = log.ToString();
        }
        catch (Exception ex)
        {
            ProgressText += $"错误: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
        var proxy = ProxyUrl.Trim();
        if (!proxy.IsNullOrEmpty())
        {
            handler.Proxy = new WebProxy(proxy);
            handler.UseProxy = true;
        }
        var client = new HttpClient(handler)
        {
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
        };
        client.Timeout = TimeSpan.FromSeconds(15);
        return client;
    }

    private static string BuildDohTarget(string dohUrl)
    {
        // 若用户给的是完整 dns-query 端点, 直接使用; 否则拼接 /dns-query
        if (dohUrl.Contains("dns-query", StringComparison.OrdinalIgnoreCase)
            || dohUrl.Contains('?'))
        {
            return dohUrl;
        }
        return dohUrl.TrimEnd('/') + "/dns-query";
    }

    private async Task<List<DohResultItem>> ResolveAsync(HttpClient client, string target, string domain, DnsType type)
    {
        var request = BuildRequest(domain, type);
        using var content = new ByteArrayContent(request);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");
        using var req = new HttpRequestMessage(HttpMethod.Post, target) { Content = content };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-message"));
        req.Version = HttpVersion.Version20;

        using var resp = await client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsByteArrayAsync();
        return ParseResponse(body, type);
    }

    private static byte[] BuildRequest(string domain, DnsType type)
    {
        var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        var id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
        bw.Write((ushort)IPAddress.HostToNetworkOrder((short)id)); // ID
        bw.Write((ushort)0x0100); // flags: standard query, recursion desired
        bw.Write((ushort)IPAddress.HostToNetworkOrder((short)1)); // QDCOUNT
        bw.Write((ushort)0); // ANCOUNT
        bw.Write((ushort)0); // NSCOUNT
        bw.Write((ushort)0); // ARCOUNT

        foreach (var label in domain.Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            bw.Write((byte)bytes.Length);
            bw.Write(bytes);
        }
        bw.Write((byte)0); // root label

        bw.Write((ushort)IPAddress.HostToNetworkOrder((short)type)); // QTYPE
        bw.Write((ushort)IPAddress.HostToNetworkOrder((short)1)); // QCLASS IN
        return ms.ToArray();
    }

    private static List<DohResultItem> ParseResponse(byte[] body, DnsType queryType)
    {
        var result = new List<DohResultItem>();
        if (body.Length < 12)
        {
            return result;
        }
        var reader = new BinaryReader(new MemoryStream(body));
        reader.ReadUInt16(); // ID
        reader.ReadUInt16(); // flags
        var qd = IPAddress.NetworkToHostOrder((short)reader.ReadUInt16());
        var an = IPAddress.NetworkToHostOrder((short)reader.ReadUInt16());
        reader.ReadUInt16(); // ns
        reader.ReadUInt16(); // ar

        // skip questions
        for (var i = 0; i < qd; i++)
        {
            SkipName(reader);
            reader.ReadUInt16(); // type
            reader.ReadUInt16(); // class
        }

        for (var i = 0; i < an; i++)
        {
            SkipName(reader);
            var type = (DnsType)IPAddress.NetworkToHostOrder((short)reader.ReadUInt16());
            reader.ReadUInt16(); // class
            var ttl = IPAddress.NetworkToHostOrder((int)reader.ReadUInt32());
            var rdLength = IPAddress.NetworkToHostOrder((short)reader.ReadUInt16());
            if ((type == DnsType.A && rdLength == 4) || (type == DnsType.AAAA && rdLength == 16))
            {
                var addrBytes = reader.ReadBytes(rdLength);
                result.Add(new DohResultItem
                {
                    Type = type.ToString(),
                    Address = new IPAddress(addrBytes).ToString(),
                    Ttl = ttl,
                });
            }
            else
            {
                reader.ReadBytes(rdLength);
            }
        }
        return result;
    }

    private static void SkipName(BinaryReader reader)
    {
        while (true)
        {
            var len = reader.ReadByte();
            if (len == 0)
            {
                return;
            }
            if ((len & 0xC0) == 0xC0) // compression pointer
            {
                reader.ReadByte();
                return;
            }
            reader.ReadBytes(len);
        }
    }

    private enum DnsType : ushort
    {
        A = 1,
        AAAA = 28,
    }
}
