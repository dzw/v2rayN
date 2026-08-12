# -*- coding: utf-8 -*-
"""
explore_plugin_gist.py — BluesYoung-web 免费节点 gist 探索插件

访问 https://gist.github.com/BluesYoung-web , 找出 free-node-2026_*.yaml
中最新的一份 (按文件名内嵌的时间戳), 下载原始 YAML 并转换为分享链接。
"""
import json
import re
import time
import urllib.parse

import explore_plugin_base as base

GIST_USER = "BluesYoung-web"
GIST_URL = f"https://gist.github.com/{GIST_USER}"
RAW_BASE = "https://gist.githubusercontent.com"
API_URL = f"https://api.github.com/users/{GIST_USER}/gists?per_page=100"

# 文件名形如 free-node-2026_8_11_21_08_02.yaml -> 解析时间戳排序
FNAME_RE = re.compile(
    r"free-node-(\d{4})_(\d{1,2})_(\d{1,2})_(\d{1,2})_(\d{1,2})_(\d{1,2})\.ya?ml",
    re.IGNORECASE,
)


def _parse_fname(name):
    m = FNAME_RE.search(name)
    if not m:
        return None
    y, mo, d, h, mi, s = (int(x) for x in m.groups())
    return (y * 10**10) + (mo * 10**8) + (d * 10**6) + (h * 10**4) + (mi * 10**2) + s


def _list_gist_files(opener):
    """返回 [(filename, raw_url), ...] 按时间倒序 (最新在前)。"""
    out = []
    html = base.fetch_text(opener, GIST_URL)
    # gist 页面会内联 file-* 链接; 也尝试 api
    if html:
        for m in re.finditer(r'href="(/%s/[a-f0-9]+/raw/[^"]+)"' % GIST_USER, html):
            href = m.group(1)
            fn = href.rstrip("/").split("/")[-1]
            if _parse_fname(fn) is not None:
                out.append((fn, "https://gist.github.com" + href))
    if not out:
        try:
            api = base.fetch_text(opener, API_URL)
            for g in json.loads(api):
                for fn, meta in (g.get("files") or {}).items():
                    if _parse_fname(fn) is not None:
                        out.append((fn, meta.get("raw_url", "")))
        except Exception:
            pass
    out.sort(key=lambda x: _parse_fname(x[0]) or 0, reverse=True)
    return out


def _yaml_to_links(opener, raw_url):
    ytext = base.fetch_text(opener, raw_url)
    if not ytext:
        return []
    # 优先用 pyyaml 结构化解析
    try:
        import yaml

        data = yaml.safe_load(ytext)
        if isinstance(data, dict) and data.get("proxies"):
            return base.clash_to_share_links(data["proxies"])
    except Exception:
        pass
    # 退化: 直接抽链接
    return base.extract_links(ytext)


class GistPlugin(base.BasePlugin):
    name = "gist"

    def discover(self, opener, keys, proxy=None):
        files = _list_gist_files(opener)
        if not files:
            print("[gist] no free-node yaml found", file=__import__("sys").stderr)
            return
        latest_fn, latest_url = files[0]
        print(f"[gist] latest: {latest_fn} ({latest_url})", file=__import__("sys").stderr)
        for link in _yaml_to_links(opener, latest_url):
            yield link
        # 也顺带解析其余 (可选, 控制数量)
        for fn, url in files[1:4]:
            time.sleep(0.5)
            for link in _yaml_to_links(opener, url):
                yield link
