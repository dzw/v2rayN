# -*- coding: utf-8 -*-
"""
explore_plugin_clashgithub.py — ClashGithub (clashnode) 探索插件

流程:
 1. 访问 https://clashgithub.com/category/freenode 分类页
 2. 找出所有 clashnode-YYYYMMDD.html 链接, 按日期挑最新的
 3. 解析该页面里的 hysteria2:// ss:// trojan:// vless:// vmess:// 分享链接
    及 .yaml 订阅

也支持 sources.clashgithub_pages 里手动追加的页面。
"""
import re
import time

import explore_plugin_base as base

CATEGORY_URL = "https://clashgithub.com/category/freenode"
NODE_LINK_RE = re.compile(
    r"https?://clashgithub\.com/clashnode-(\d{4})(\d{2})(\d{2})\.html",
    re.IGNORECASE,
)


def find_newest_node_pages(opener, limit=1):
    """返回按日期倒序的 clashnode-*.html 列表 (最新在前)。"""
    html = base.fetch_text(opener, CATEGORY_URL)
    found = []
    if html:
        for m in NODE_LINK_RE.finditer(html):
            y, mo, d = m.groups()
            ts = int(f"{y}{mo}{d}")
            found.append((ts, m.group(0)))
    # 也可能以相对路径出现
    if html and not found:
        for m in re.finditer(r"/clashnode-(\d{4})(\d{2})(\d{2})\.html", html, re.IGNORECASE):
            y, mo, d = m.groups()
            ts = int(f"{y}{mo}{d}")
            found.append((ts, "https://clashgithub.com/clashnode-" + m.group(1) + ".html"))
    found.sort(key=lambda x: x[0], reverse=True)
    return [u for _, u in found[:limit]]


def parse_node_page(opener, url):
    results = []
    html = base.fetch_text(opener, url)
    if not html:
        return results
    results += base.extract_links(html)
    for yurl in base.YAML_URL_PATTERN.findall(html):
        time.sleep(0.4)
        ytext = base.fetch_text(opener, yurl)
        if ytext and ("proxies:" in ytext or "vless" in ytext):
            try:
                import yaml

                data = yaml.safe_load(ytext)
                if isinstance(data, dict) and data.get("proxies"):
                    results += base.clash_to_share_links(data["proxies"])
                    continue
            except Exception:
                pass
            results += base.extract_links(ytext)
    return results


class ClashGithubPlugin(base.BasePlugin):
    name = "clashgithub"

    # 默认保底页面 (分类页抓取失败时回退)
    DEFAULT_PAGES = [
        "https://clashgithub.com/clashnode-20260807.html",
    ]

    def discover(self, opener, keys, proxy=None):
        extra = []
        if isinstance(proxy, dict):
            extra = proxy.get("clashgithub_pages") or []

        pages = []
        # 1) 分类页自动找最新
        try:
            pages += find_newest_node_pages(opener, limit=3)
        except Exception as ex:  # noqa: BLE001
            print(f"[warn] clashgithub category fail: {ex}", file=__import__("sys").stderr)
        # 2) 手动追加页面
        for s in extra:
            if s not in pages:
                pages.append(s)
        # 3) 保底默认页 (去重)
        for s in self.DEFAULT_PAGES:
            if s not in pages:
                pages.append(s)

        seen = set()
        for url in pages:
            print(f"[clashgithub] parse {url}", file=__import__("sys").stderr)
            for link in parse_node_page(opener, url):
                if link not in seen:
                    seen.add(link)
                    yield link
            time.sleep(0.3)
