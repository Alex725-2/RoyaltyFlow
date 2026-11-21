import unittest
from unittest.mock import patch

from check_dashboard import verify_dashboard


LOCAL = "http://127.0.0.1:18080"
PUBLIC = "https://portfolio.test"
INDEX = b"""<meta name="application-name" content="RoyaltyFlow Dashboard">
<script type="module" src="/assets/index-build.js"></script>
<link rel="stylesheet" href="/assets/index-build.css">"""


class Response:
    def __init__(self, mime, content, status=200):
        self.status = status
        self.headers = type("Headers", (), {"get_content_type": lambda _: mime})()
        self.content = content

    def __enter__(self):
        return self

    def __exit__(self, *_):
        pass

    def read(self):
        return self.content


class DashboardCheckTests(unittest.TestCase):
    def setUp(self):
        self.pages = {}
        for origin in (LOCAL, PUBLIC):
            self.pages[origin + "/"] = Response("text/html", INDEX)
            self.pages[origin + "/assets/index-build.js"] = Response("text/javascript", b"export {}")
            self.pages[origin + "/assets/index-build.css"] = Response("text/css", b"body {}")
        patcher = patch("check_dashboard.build_opener")
        opener = patcher.start()
        opener.return_value.open.side_effect = lambda url, timeout: self.pages[url]
        self.addCleanup(patcher.stop)

    def test_matching_dashboard_and_assets(self):
        verify_dashboard(LOCAL, PUBLIC)

    def test_rollout_must_reject_stale_public_html(self):
        self.pages[PUBLIC + "/"] = Response("text/html", INDEX.replace(b"index-build", b"older-build"))
        with self.assertRaisesRegex(ValueError, "different Dashboard page"):
            verify_dashboard(LOCAL, PUBLIC)

    def test_rollout_must_reject_html_fallback_for_javascript(self):
        self.pages[PUBLIC + "/assets/index-build.js"] = Response("text/html", INDEX)
        with self.assertRaisesRegex(ValueError, "Unexpected response"):
            verify_dashboard(LOCAL, PUBLIC)

    def test_html_cannot_direct_verifier_to_another_origin(self):
        for origin in (LOCAL, PUBLIC):
            self.pages[origin + "/"] = Response("text/html", INDEX.replace(b"/assets/index-build.js", b"//other.test/app.js"))
        with self.assertRaisesRegex(ValueError, "local Vite asset paths"):
            verify_dashboard(LOCAL, PUBLIC)


if __name__ == "__main__":
    unittest.main()
