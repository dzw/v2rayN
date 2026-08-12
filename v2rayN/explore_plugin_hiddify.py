# -*- coding: utf-8 -*-
"""
explore_plugin_hiddify.py — Hiddify 免费节点分享探索插件

Hiddify 文档页 https://hiddify.me/docs/Tutorial/hiddify-next-free-node-sharing/
内含 vmess:// (以及其它协议) 分享链接, 也可能嵌入 .yaml 订阅。
"""
import time

import explore_plugin_base as base

DEFAULT_URL = "https://hiddify.me/docs/Tutorial/hiddify-next-free-node-sharing/"

# 文档页内常嵌入订阅链接 (sub/), 抓到后作为整体订阅交给 v2rayN 解析
SUBSCRIPTION_HINT = re.compile(r"https?://[^\s\"'<>]+(?:sub|subscription|api)[^\s\"'<>]*", re.IGNORECASE)
import re


class HiddifyPlugin(base.BasePlugin):
    name = "hiddify"

    def discover(self, opener, keys, proxy=None):
        html = base.fetch_text(opener, DEFAULT_URL)
        if not html:
            return
        for link in base.extract_links(html):
            yield link
        # 抓取内嵌订阅链接并下载, 解析其中节点
        for sub in set(SUBSCRIPTION_HINT.findall(html)):
            time.sleep(0.5)
            text = base.fetch_text(opener, sub)
            if not text:
                continue
            if "proxies:" in text:
                try:
                    import yaml

                    data = yaml.safe_load(text)
                    if isinstance(data, dict) and data.get("proxies"):
                        for ln in base.clash_to_share_links(data["proxies"]):
                            yield ln
                        continue
                except Exception:
                    pass
            for ln in base.extract_links(text):
                yield ln
