import sys
import xml.etree.ElementTree as ET
from pathlib import Path

p = Path(sys.argv[1])
assert p.name == "00-system-overview-fig2-pass-05.drawio"
tree = ET.parse(p)
cells = {c.get("id"): c for c in tree.findall(".//mxCell")}
cells["review-subtitle"].set("value", "Nonempty diff, no further Rai revision")
cells["rai-to-review"].set("value", "no revision")
cells["terminal-metadata"].set("value", "RunWatchLoopService:595–681")
tree.write(p, encoding="utf-8", xml_declaration=True)
