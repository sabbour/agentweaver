"""Check owned Markdown targets and fragments against the completed VitePress build."""
import json
import re
import sys
from html.parser import HTMLParser
from pathlib import Path
from urllib.parse import unquote, urlsplit

here = Path(__file__).resolve().parent
repo = here.parents[3]
docs = repo / "docs"
dist = docs / ".vitepress/dist"
plan = json.loads((repo / ".github/skills/docs-diagram-audit/reports/plan-deep-dive-core.json").read_text())
class IDs(HTMLParser):
    def __init__(self):
        super().__init__()
        self.ids = set()
    def handle_starttag(self, tag, attrs):
        self.ids.update(value for name, value in attrs if name == "id")

errors = []
checked = 0
provenance = []
for relative in plan["document_paths"]:
    source = repo / relative
    text = source.read_text(encoding="utf-8")
    text = re.sub(r"```[\s\S]*?```", "", text)
    for value in re.findall(r"!?\[[^\]]+\]\(([^)\n]+)\)", text):
        target = urlsplit(value.strip().split(" ")[0].strip("<>"))
        if target.scheme or target.netloc:
            continue
        checked += 1
        pathname = unquote(target.path)
        base = (docs / pathname.lstrip("/")) if pathname.startswith("/") else source.parent / pathname
        if not pathname:
            base = source
        candidates = [base, base.with_suffix(".md"), base / "index.md", base / "README.md",
                      docs / "public" / pathname.lstrip("/")]
        found = next((path.resolve() for path in candidates if path.is_file()), None)
        if found is None:
            errors.append({"source": relative, "target": value, "error": "missing target"})
            continue
        if target.fragment and found.suffix == ".md" and found.is_relative_to(docs):
            output = dist / found.relative_to(docs).with_suffix(".html")
            if not output.is_file():
                errors.append({"source": relative, "target": value, "error": "missing built page"})
                continue
            parser = IDs()
            parser.feed(output.read_text(encoding="utf-8"))
            if unquote(target.fragment) not in parser.ids:
                errors.append({"source": relative, "target": value, "error": "missing rendered anchor"})
    for name in re.findall(r"\.\./diagrams/src/([a-z0-9-]+\.(?:json|drawio))", text):
        if not (docs / "diagrams/src" / name).is_file():
            provenance.append({"source": relative, "target": name})
report = {"local_links_checked": checked, "errors": errors, "stale_provenance_comments": provenance}
(here / "link-validation.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
print(json.dumps(report, indent=2))
sys.exit(bool(errors or provenance))
