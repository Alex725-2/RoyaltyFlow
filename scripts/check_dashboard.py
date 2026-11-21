"""Verify that the local and public origins serve the same Dashboard build."""

import re
import sys
from html.parser import HTMLParser
from urllib.request import ProxyHandler, build_opener

from smoke_test import NoRedirect


class DashboardHtml(HTMLParser):
    def __init__(self):
        super().__init__()
        self.is_dashboard = False
        self.modules = []
        self.styles = []

    def handle_starttag(self, tag, attrs):
        values = dict(attrs)
        if tag == "meta" and values.get("name") == "application-name":
            self.is_dashboard = values.get("content") == "RoyaltyFlow Dashboard"
        elif tag == "script" and values.get("type") == "module":
            self.modules.append(values.get("src"))
        elif tag == "link" and values.get("rel") == "stylesheet":
            self.styles.append(values.get("href"))


def verify_dashboard(local_origin, public_origin):
    opener = build_opener(ProxyHandler({}), NoRedirect())

    def read(origin, path, accepted_types):
        with opener.open(origin + path, timeout=10) as response:
            if response.status != 200 or response.headers.get_content_type() not in accepted_types:
                raise ValueError(f"Unexpected response for Dashboard path {path}.")
            content = response.read()
        if not content:
            raise ValueError(f"Empty response for Dashboard path {path}.")
        return content

    page = read(local_origin, "/", {"text/html"})
    parser = DashboardHtml()
    parser.feed(page.decode("utf-8"))
    if not (parser.is_dashboard and parser.modules and parser.styles):
        raise ValueError("Dashboard HTML or entry points are missing at the local origin.")
    if page != read(public_origin, "/", {"text/html"}):
        raise ValueError("The public origin returned a different Dashboard page.")
    for paths, suffix, types in (
        (parser.modules, "js", {"application/javascript", "text/javascript"}),
        (parser.styles, "css", {"text/css"}),
    ):
        for path in paths:
            if not path or not re.fullmatch(rf"/assets/[\w.-]+\.{suffix}", path, flags=re.ASCII):
                raise ValueError("Dashboard entry points must use local Vite asset paths.")
            if read(local_origin, path, types) != read(public_origin, path, types):
                raise ValueError(f"The public origin returned a different Dashboard asset at {path}.")


if __name__ == "__main__":
    try:
        verify_dashboard(*sys.argv[1:])
    except (OSError, ValueError) as error:
        print(f"Dashboard check failed: {error}", file=sys.stderr)
        sys.exit(1)
    print("Dashboard HTML, JavaScript and CSS match on the local and public origins.")
