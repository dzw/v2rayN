# -*- coding: utf-8 -*-
"""
explore_plugin_google.py — 搜索引擎探索插件 (qBittorrent 风格)

利用已有节点 key 作为关键词, 在 Google / DuckDuckGo 搜索,
抓取命中页面并解析出分享链接与 .yaml 订阅。
"""
import time
import urllib.parse

import explore_plugin_base as base

RESULTS_PER_PAGE = 10
SLEEP_BETWEEN = 1.0


def google_search_urls(opener, query, pages=3):
    found = []
    for p in range(pages):
        start = p * 10
        url = (
            f"https://www.google.com/search?q={urllib.parse.quote(query)}"
            f"&start={start}&num={RESULTS_PER_PAGE}"
        )
        html = base.fetch_text(opener, url)
        if not html:
            break
        for m in __import__("re").finditer(r"/url\?q=([^&]+)&", html):
            real = urllib.parse.unquote(m.group(1))
            if real.startswith("http") and "google.com" not in real:
                found.append(real)
        if not found:
            found += __import__("re").findall(
                r"https?://(?!www\.google\.com|www\.googleusercontent\.com)[^\s\"'<>]+",
                html,
            )
        time.sleep(SLEEP_BETWEEN)
    return found[: RESULTS_PER_PAGE * pages]


def duckduckgo_search_urls(opener, query, pages=3):
    found = []
    for _ in range(pages):
        url = f"https://html.duckduckgo.com/html/?q={urllib.parse.quote(query)}"
        html = base.fetch_text(opener, url)
        if not html:
            break
        for m in __import__("re").finditer(r"uddg=([^\"&]+)", html):
            found.append(urllib.parse.unquote(m.group(1)))
        time.sleep(SLEEP_BETWEEN)
    return found[: RESULTS_PER_PAGE * pages]


class GooglePlugin(base.BasePlugin):
    name = "google"

    def discover(self, opener, keys, proxy=None):
        seen = set()
        for k in keys:
            urls = []
            urls += google_search_urls(opener, k)
            if not urls:
                urls += duckduckgo_search_urls(opener, k)
            time.sleep(SLEEP_BETWEEN)
            for u in urls:
                if u in seen:
                    continue
                seen.add(u)
                html = base.fetch_text(opener, u)
                if not html:
                    continue
                for link in base.extract_links(html):
                    yield link
                for yurl in base.YAML_URL_PATTERN.findall(html):
                    ytext = base.fetch_text(opener, yurl)
                    if ytext and ("proxies:" in ytext or "vless" in ytext):
                        for ln in _yaml_links(ytext):
                            yield ln
                        time.sleep(SLEEP_BETWEEN)


def _yaml_links(ytext):
    try:
        import yaml

        data = yaml.safe_load(ytext)
        proxies = data.get("proxies") if isinstance(data, dict) else None
        if proxies:
            return base.clash_to_share_links(proxies)
    except Exception:
        pass
    return base.extract_links(ytext)
