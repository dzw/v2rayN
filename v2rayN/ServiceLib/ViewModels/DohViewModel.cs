using System.Buffers;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ReactiveUI;

namespace ServiceLib.ViewModels;

public partial class DohResultItem : MyReactiveObject
{
    [Reactive] public string Domain { get; set; } = string.Empty;
    [Reactive] public string Type { get; set; } = string.Empty;
    [Reactive] public string Address { get; set; } = string.Empty;
    [Reactive] public int Ttl { get; set; }
}

public partial class DohViewModel : MyReactiveObject
{
    [Reactive] public string Domains { get; set; } = string.Empty;
    [Reactive] public string DohUrl { get; set; } = "https://1.1.1.1/dns-query";
    [Reactive] public string ProxyUrl { get; set; } = string.Empty;
    [Reactive] public int ResolveType { get; set; } = 0; // 0=A, 1=AAAA, 2=Both
    [Reactive] public ObservableCollection<DohResultItem> Results { get; set; } = new();
    [Reactive] public string ProgressText { get; set; } = string.Empty;
    [Reactive] public bool IsBusy { get; set; }

    public ReactiveCommand<RxVoid, RxVoid> QueryCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> CopyResultsCmd { get; }

    public EventChannel<string> CopyRequested { get; } = new();

    public DohViewModel()
    {
        QueryCmd = ReactiveCommand.CreateFromTask(async () => await QueryAsync());
        CopyResultsCmd = ReactiveCommand.Create(() =>
        {
            if (Results.Count == 0)
            {
                return;
            }
            var text = string.Join(Environment.NewLine, Results.Select(r => r.Address));
            CopyRequested.Publish(text);
        });
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
        var dohUrl = DohUrl.Trim();
        if (domains.Count == 0 || dohUrl.IsNullOrEmpty())
        {
            ProgressText = "请填写域名与 DoH 地址";
            return;
        }

        IsBusy = true;
        try
        {
            var target = BuildDohTarget(dohUrl);
            var types = ResolveType switch
            {
                1 => new[] { DnsType.AAAA },
                2 => new[] { DnsType.A, DnsType.AAAA },
                _ => new[] { DnsType.A },
            };

            using var client = CreateClient();
            foreach (var domain in domains)
            {
                foreach (var t in types)
                {
                    ProgressText += $"查询 {domain} {t} ...{Environment.NewLine}";
                    try
                    {
                        var answers = await ResolveAsync(client, target, domain, t);
                        foreach (var a in answers)
                        {
                            a.Domain = domain;
                            Results.Add(a);
                            ProgressText += $"  {a.Type} {a.Address} (ttl={a.Ttl}){Environment.NewLine}";
                        }
                        if (answers.Count == 0)
                        {
                            ProgressText += $"  无记录{Environment.NewLine}";
                        }
                    }
                    catch (Exception ex)
                    {
                        ProgressText += $"  失败: {ex.Message}{Environment.NewLine}";
                    }
                }
            }
            ProgressText += "完成";
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
