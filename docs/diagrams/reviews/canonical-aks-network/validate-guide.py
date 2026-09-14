"""Guide-owned placeholder retirement and built-page link/image validation."""
import hashlib
from html.parser import HTMLParser
import json
from pathlib import Path
import sys
from urllib.parse import unquote, urlsplit

from PIL import Image

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[3]
PLAN = json.loads((ROOT / ".github/skills/docs-diagram-audit/reports/plan-guide.json").read_text())


def save(name, value):
    (HERE / name).write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")


class Page(HTMLParser):
    def __init__(self):
        super().__init__()
        self.in_main = False
        self.links = []
        self.images = []
        self.ids = set()

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if "id" in attrs:
            self.ids.add(attrs["id"])
        if tag == "main":
            self.in_main = True
        if self.in_main and tag == "a" and "href" in attrs:
            self.links.append(attrs["href"])
        if self.in_main and tag == "img" and "src" in attrs:
            self.images.append(attrs["src"])

    def handle_endtag(self, tag):
        if tag == "main":
            self.in_main = False


if sys.argv[1] == "placeholders":
    report = []
    sources = [(p, p.read_text(encoding="utf-8")) for p in (ROOT / "docs/guide").glob("*.md")]
    for relative in PLAN["exclusive_asset_paths"]:
        if not relative.startswith("docs/public/guide/images/"):
            continue
        target = ROOT / relative
        with Image.open(target) as image:
            size = image.size
        assert size == (1, 1), f"Refusing to delete real image: {target} {size}"
        for source, text in sources:
            assert f"/guide/images/{target.name}" not in text, source
            assert f"images/{target.name}" not in text, source
        report.append({"path": relative, "width": size[0], "height": size[1],
                       "sha256": hashlib.sha256(target.read_bytes()).hexdigest(),
                       "action": "removed verified placeholder; UI prose retained"})
    save("placeholder-retirement.json", report)
    for item in report:
        (ROOT / item["path"]).unlink()
    print(f"Removed exactly {len(report)} allowlisted 1x1 PNGs after reference checks.")
elif sys.argv[1] == "links":
    dist = ROOT / "docs/.vitepress/dist"
    cache = {}
    failures = []
    checked = []

    def page(path):
        if path not in cache:
            parser = Page()
            parser.feed(path.read_text(encoding="utf-8"))
            cache[path] = parser
        return cache[path]

    for relative in PLAN["document_paths"]:
        source = dist / Path(relative).relative_to("docs").with_suffix(".html")
        parsed = page(source)
        checked.append({"document": relative, "links": len(parsed.links), "images": len(parsed.images)})
        for is_image, url in [(False, u) for u in parsed.links] + [(True, u) for u in parsed.images]:
            split = urlsplit(url)
            if split.scheme or split.netloc:
                continue
            route = unquote(split.path)
            if route.startswith("/agentweaver/"):
                route = route[len("/agentweaver/"):]
                target = dist / route
            elif route.startswith("/"):
                target = dist / route.lstrip("/")
            elif not route:
                target = source
            else:
                target = source.parent / route
            if target.is_dir():
                target = target / "index.html"
            elif not target.suffix:
                target = target.with_suffix(".html")
            if not target.is_file():
                failures.append({"document": relative, "url": url, "error": "missing built target"})
                continue
            if split.fragment and target.suffix == ".html":
                if unquote(split.fragment) not in page(target).ids:
                    failures.append({"document": relative, "url": url, "error": "missing anchor"})
            if is_image:
                with Image.open(target) as image:
                    assert image.width > 1 and image.height > 1, target
    save("link-validation.json", {"documents": checked, "failures": failures})
    print(json.dumps({"pages": len(checked), "links": sum(p["links"] for p in checked),
                      "images": sum(p["images"] for p in checked), "failures": failures}, indent=2))
    sys.exit(bool(failures))
else:
    raise ValueError("Use placeholders or links")
