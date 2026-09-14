"""Compose equal-size evidence and hide panel identities for the confirmation round."""
import hashlib
import json
from pathlib import Path
import secrets
from PIL import Image

HERE = Path(__file__).resolve().parent
candidate_name = "candidate-final.png" if (HERE / "candidate-final.png").exists() else "candidate.png"
names = ["original-react.png", candidate_name]
images = [Image.open(HERE / name).convert("RGB") for name in names]
assert all(image.size == (2140, 2018) for image in images)


def pair(left, right, filename):
    result = Image.new("RGB", (4280, 2018), "white")
    result.paste(left, (0, 0))
    result.paste(right, (2140, 0))
    result.save(HERE / filename)


comparison_name = "comparison-final.png" if candidate_name == "candidate-final.png" else "comparison.png"
pair(*images, comparison_name)
order = [0, 1] if secrets.randbelow(2) == 0 else [1, 0]
for label, index in zip(("A", "B"), order):
    images[index].save(HERE / f"blind-{label}.png")
pair(images[order[0]], images[order[1]], "blind-comparison.png")
(HERE / "blind-key.json").write_text(json.dumps({
    label: {"source": names[index], "sha256": hashlib.sha256((HERE / names[index]).read_bytes()).hexdigest()}
    for label, index in zip(("A", "B"), order)
}, indent=2) + "\n")
print("Comparison panels: 2140x2018 each; composed image: 4280x2018. No resizing.")
print("Blind identities withheld in blind-key.json until assessments are recorded.")
