#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
explore_nodes.py — v2rayN 节点探索主调度脚本 (后台采集新节点)

用法:
    python explore_nodes.py <keys.json> <output.txt> [proxy_url] [sources.json]

参数:
    keys.json    v2rayN 导出的节点 key 列表 (JSON array[str])
    output.txt   解析到的分享链接逐行写回此文件 (每条一行, 追加)
    proxy_url    可选, 抓取使用的 HTTP/HTTPS 代理, 例如 http://127.0.0.1:7890
                 也可通过环境变量 EXPLORE_PROXY 设置
    sources.json 可选, 启用的探索插件配置 (JSON), 形如:
        {
          "gist": true,
          "hiddify": true,
          "clashgithub": true,
          "google": true,
          "share_sites": ["https://example.com/nodes.html", ...]
        }
        缺省时使用内置默认 (全部启用)

探索插件 (类似 qBittorrent 搜索插件, 每个一个独立 python 文件):
    - explore_plugin_gist.py          : BluesYoung-web gist 免费节点 YAML
    - explore_plugin_hiddify.py       : Hiddify 免费节点分享页
    - explore_plugin_clashgithub.py   : ClashGithub / clashnode 节点页
    - explore_plugin_google.py        : 用现有节点 key 在搜索引擎发现新源

每个发现的分享链接会立即追加写入 output.txt; 进度信息输出到 stderr。
注意: v2rayN 目前不支持 ssr://, 脚本仍会写出, 由 v2rayN 端过滤。
"""

import json
import os
import sys
import time

import explore_plugin_base as base

from explore_plugin_gist import GistPlugin
from explore_plugin_hiddify import HiddifyPlugin
from explore_plugin_clashgithub import ClashGithubPlugin
from explore_plugin_google import GooglePlugin


def load_plugins():
    return {
        "gist": GistPlugin(),
        "hiddify": HiddifyPlugin(),
        "clashgithub": ClashGithubPlugin(),
        "google": GooglePlugin(),
    }


def main():
    if len(sys.argv) < 3:
        print(
            "usage: explore_nodes.py <keys.json> <output.txt> [proxy] [sources.json]",
            file=sys.stderr,
        )
        sys.exit(2)

    keys_path = sys.argv[1]
    out_path = sys.argv[2]
    proxy = sys.argv[3] if len(sys.argv) > 3 and sys.argv[3] else None
    sources_path = sys.argv[4] if len(sys.argv) > 4 and sys.argv[4] else None

    with open(keys_path, "r", encoding="utf-8") as f:
        keys = json.load(f)
    if not isinstance(keys, list):
        keys = [keys]

    cfg = {
        "gist": True,
        "hiddify": True,
        "clashgithub": True,
        "google": True,
        "share_sites": [],
    }
    if sources_path and os.path.exists(sources_path):
        try:
            with open(sources_path, "r", encoding="utf-8") as f:
                user = json.load(f)
            if isinstance(user, dict):
                cfg.update({k: v for k, v in user.items() if k in cfg})
        except Exception as ex:  # noqa: BLE001
            print(f"[warn] load sources failed: {ex}", file=sys.stderr)

    opener = base.build_opener(proxy)
    plugins = load_plugins()

    seen = set()
    total = 0

    def emit(link):
        nonlocal total
        if link not in seen:
            seen.add(link)
            with open(out_path, "a", encoding="utf-8") as fo:
                fo.write(link + "\n")
            total += 1
            print(f"[found] {link}", file=sys.stderr)

    def run_plugin(plugin_key):
        plugin = plugins.get(plugin_key)
        if not plugin:
            return
        if not cfg.get(plugin_key, False):
            print(f"[skip] plugin {plugin_key} disabled", file=sys.stderr)
            return
        print(f"[plugin] running {plugin_key} ...", file=sys.stderr)
        try:
            for link in plugin.discover(opener, keys, proxy=cfg):
                emit(link)
        except Exception as ex:  # noqa: BLE001
            print(f"[warn] plugin {plugin_key} error: {ex}", file=sys.stderr)

    # 1) 直接分享站 / 已知页面插件
    for key in ("gist", "hiddify", "clashgithub"):
        run_plugin(key)
        time.sleep(0.3)

    # 2) 用户自定义 share_sites (直接抓取, 抽链接 + yaml)
    for site in cfg.get("share_sites") or []:
        html = base.fetch_text(opener, site)
        if not html:
            continue
        for link in base.extract_links(html):
            emit(link)
        for yurl in base.YAML_URL_PATTERN.finditer(html):
            ytext = base.fetch_text(opener, yurl.group(0))
            if ytext and ("proxies:" in ytext or "vless" in ytext):
                try:
                    import yaml

                    data = yaml.safe_load(ytext)
                    if isinstance(data, dict) and data.get("proxies"):
                        for ln in base.clash_to_share_links(data["proxies"]):
                            emit(ln)
                        continue
                except Exception:
                    pass
                for ln in base.extract_links(ytext):
                    emit(ln)

    # 3) 搜索引擎插件 (用现有 key 发现新源)
    run_plugin("google")

    print(f"[done] total={total}", file=sys.stderr)


if __name__ == "__main__":
    main()
