# -*- coding: utf-8 -*-
"""
explore_plugin_base.py — v2rayN 节点探索插件公共基类与工具

所有探索插件需继承 BasePlugin, 实现 discover() 方法:
    - 接收 opener(已配置代理) 与 keys(list[str])
    - 通过 yield 持续产出分享链接字符串 (vless:// vmess:// ss:// trojan:// hysteria2://)

以及提供 Clash YAML -> 分享链接 的通用转换函数。
"""

import base64
import json
import re
import urllib.parse

# 允许的分享链接协议 (ssr 由 v2rayN 端过滤)
LINK_PATTERNS = [
    re.compile(r"vless://[^\s\"'<>]+", re.IGNORECASE),
    re.compile(r"vmess://[^\s\"'<>]+", re.IGNORECASE),
    re.compile(r"ss://[^\s\"'<>]+", re.IGNORECASE),
    re.compile(r"ssr://[^\s\"'<>]+", re.IGNORECASE),
    re.compile(r"trojan://[^\s\"'<>]+", re.IGNORECASE),
    re.compile(r"hysteria2://[^\s\"'<>]+", re.IGNORECASE),
]
YAML_URL_PATTERN = re.compile(
    r"https?://[^\s\"'<>]+\.ya?ml(?:\?[^ \s\"'<>]*)?", re.IGNORECASE
)


def extract_links(text):
    """从任意文本中抽取全部分享链接 (去重由调用方负责)。"""
    out = []
    for pat in LINK_PATTERNS:
        out += pat.findall(text)
    return out


def build_opener(proxy_url=None):
    import urllib.request

    handlers = []
    if proxy_url:
        handlers.append(
            urllib.request.ProxyHandler({"http": proxy_url, "https": proxy_url})
        )
    opener = urllib.request.build_opener(*handlers)
    opener.addheaders = [
        (
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
            "(KHTML, like Gecko) Chrome/124.0 Safari/537.36",
        )
    ]
    return opener


def fetch_text(opener, url, timeout=20):
    import urllib.request

    try:
        with opener.open(url, timeout=timeout) as resp:
            raw = resp.read()
            charset = resp.headers.get_content_charset() or "utf-8"
            try:
                return raw.decode(charset, errors="replace")
            except LookupError:
                return raw.decode("utf-8", errors="replace")
    except Exception as ex:  # noqa: BLE001
        print(f"[warn] fetch failed: {url} -> {ex}", file=__import__("sys").stderr)
        return ""


def _b64decode(s):
    """容错 base64 解码 (兼容 url-safe 与缺 padding)。"""
    if not s:
        return ""
    s = s.strip()
    try:
        # 优先尝试 url-safe
        return base64.urlsafe_b64decode(_pad(s)).decode("utf-8", "replace")
    except Exception:
        pass
    try:
        return base64.b64decode(_pad(s)).decode("utf-8", "replace")
    except Exception:
        return s


def _pad(s):
    return s + "=" * (-len(s) % 4)


def _fmt_name(remarks):
    return urllib.parse.quote(remarks or "node", safe="")


# ---------------------------------------------------------------------------
# Clash proxy 字典 -> 分享链接
# 支持: hysteria2 / hysteria / ss / trojan / vless / vmess
# ---------------------------------------------------------------------------
def clash_to_share_links(proxies):
    """proxies: list[dict] (解析自 Clash YAML 的 proxies 字段)。
    产出分享链接字符串列表。"""
    links = []
    for p in proxies or []:
        t = (p.get("type") or "").lower()
        name = p.get("name", "")
        try:
            if t == "hysteria2":
                links.append(_clash_hysteria2(p, name))
            elif t == "hysteria":
                links.append(_clash_hysteria(p, name))
            elif t == "ss":
                links.append(_clash_ss(p, name))
            elif t == "trojan":
                links.append(_clash_trojan(p, name))
            elif t == "vless":
                links.append(_clash_vless(p, name))
            elif t == "vmess":
                links.append(_clash_vmess(p, name))
        except Exception as ex:  # noqa: BLE001
            print(f"[warn] clash parse {t} failed: {ex}", file=__import__("sys").stderr)
    return [x for x in links if x]


def _clash_hysteria2(p, name):
    host = p["server"]
    port = p["port"]
    auth = p.get("password", "")
    q = {}
    if p.get("sni"):
        q["sni"] = p["sni"]
    if p.get("alpn"):
        q["alpn"] = _csv(p["alpn"])
    if p.get("obfs"):
        q["obfs"] = p["obfs"]
    if p.get("obfs-password"):
        q["obfs-password"] = p["obfs-password"]
    if p.get("insecure") is True:
        q["insecure"] = "1"
    qstr = urllib.parse.urlencode(q)
    frag = _fmt_name(name)
    return f"hysteria2://{auth}@{host}:{port}/?{qstr}#{frag}"


def _clash_hysteria(p, name):
    # 老版本 hysteria (协议头 hysteria:// )
    host = p["server"]
    port = p["port"]
    auth = p.get("auth-str") or p.get("password", "")
    q = {}
    if p.get("alpn"):
        q["alpn"] = _csv(p["alpn"])
    if p.get("protocol") and p.get("protocol-param"):
        q[p["protocol"]] = p["protocol-param"]
    if p.get("up") and p.get("down"):
        q["upmbps"] = p["up"]
        q["downmbps"] = p["down"]
    qstr = urllib.parse.urlencode(q)
    frag = _fmt_name(name)
    return f"hysteria://{auth}@{host}:{port}/?{qstr}#{frag}"


def _clash_ss(p, name):
    host = p["server"]
    port = p["port"]
    method = p.get("cipher", "aes-256-gcm")
    pw = p.get("password", "")
    # ss://base64(method:password)@host:port#name
    user = base64.b64encode(f"{method}:{pw}".encode()).decode()
    frag = _fmt_name(name)
    return f"ss://{user}@{host}:{port}#{frag}"


def _clash_trojan(p, name):
    host = p["server"]
    port = p["port"]
    pw = p.get("password", "")
    q = {}
    if p.get("sni"):
        q["sni"] = p["sni"]
    if p.get("alpn"):
        q["alpn"] = _csv(p["alpn"])
    if p.get("network") in ("ws", "grpc"):
        q["type"] = p["network"]
        if p.get("ws-opts", {}).get("path"):
            q["path"] = p["ws-opts"]["path"]
        if p.get("ws-opts", {}).get("headers", {}).get("Host"):
            q["host"] = p["ws-opts"]["headers"]["Host"]
    if p.get("insecure") is True:
        q["insecure"] = "1"
    qstr = urllib.parse.urlencode(q)
    frag = _fmt_name(name)
    return f"trojan://{pw}@{host}:{port}/?{qstr}#{frag}"


def _clash_vless(p, name):
    host = p["server"]
    port = p["port"]
    uuid = p.get("uuid", "")
    q = {}
    if p.get("uuid") and p.get("flow"):
        q["flow"] = p["flow"]
    net = p.get("network", "tcp")
    q["type"] = net
    if net == "ws":
        ws = p.get("ws-opts", {})
        q["path"] = ws.get("path", "/")
        if ws.get("headers", {}).get("Host"):
            q["host"] = ws["headers"]["Host"]
    elif net == "grpc":
        grpc = p.get("grpc-opts", {})
        q["serviceName"] = grpc.get("grpc-service-name", "")
    if p.get("tls") is True or p.get("tls") == "reality":
        q["security"] = "reality" if p.get("tls") == "reality" else "tls"
        if p.get("sni"):
            q["sni"] = p["sni"]
        if p.get("client-fingerprint"):
            q["fp"] = p["client-fingerprint"]
        if p.get("reality-opts", {}).get("public-key"):
            q["pbk"] = p["reality-opts"]["public-key"]
        if p.get("reality-opts", {}).get("short-id"):
            q["sid"] = p["reality-opts"]["short-id"]
    else:
        q["security"] = "none"
    qstr = urllib.parse.urlencode(q)
    frag = _fmt_name(name)
    return f"vless://{uuid}@{host}:{port}/?{qstr}#{frag}"


def _clash_vmess(p, name):
    host = p["server"]
    port = p["port"]
    cfg = {
        "v": "2",
        "ps": name,
        "add": host,
        "port": str(port),
        "id": p.get("uuid", ""),
        "aid": str(p.get("alterId", p.get("alterid", 0))),
        "scy": p.get("cipher", "auto"),
        "net": p.get("network", "tcp"),
        "type": "none",
        "tls": "tls" if p.get("tls") is True else "",
    }
    net = cfg["net"]
    if net == "ws":
        ws = p.get("ws-opts", {})
        cfg["path"] = ws.get("path", "/")
        cfg["host"] = ws.get("headers", {}).get("Host", "")
    elif net == "grpc":
        grpc = p.get("grpc-opts", {})
        cfg["path"] = grpc.get("grpc-service-name", "")
    if p.get("sni"):
        cfg["sni"] = p["sni"]
    raw = json.dumps(cfg, ensure_ascii=False)
    frag = _fmt_name(name)
    return f"vmess://{base64.b64encode(raw.encode()).decode()}#{frag}"


def _csv(v):
    if isinstance(v, list):
        return ",".join(str(x) for x in v)
    return str(v)


class BasePlugin:
    """插件基类。每个探索源继承并实现 discover()。"""

    #: 插件唯一标识 (sources 配置里以此匹配)
    name = "base"

    def discover(self, opener, keys, proxy=None):
        """生成器: 持续 yield 分享链接字符串。"""
        return
        yield  # pragma: no cover - 让 discover 成为生成器
