"""Preserve pass four and move only the temporary-ref label in pass five."""
from pathlib import Path

folder = Path(__file__).resolve().parent
name = "reference-scaling-data-layer-fig1"
source = (folder / f"{name}-pass-04.drawio").read_text(encoding="utf-8")
start = source.index('<mxCell id="temp-ref"')
end = source.index("</mxCell>", start)
before = source[start:end]
assert 'as="offset"' not in before
after = before.replace(
    "          </mxGeometry>",
    '            <mxPoint x="0" y="25" as="offset" />\n          </mxGeometry>',
)
assert before != after
target = folder / f"{name}-pass-05.drawio"
assert not target.exists()
target.write_text(source[:start] + after + source[end:], encoding="utf-8", newline="\n")
