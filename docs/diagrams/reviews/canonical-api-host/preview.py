"""Create a screen proof at A5/96 dpi from each actual Desktop PNG."""
import sys
from pathlib import Path
from PIL import Image, ImageOps

for argument in sys.argv[1:]:
    source = Path(argument)
    with Image.open(source) as image:
        proof = ImageOps.contain(image.convert("RGB"), (794, 559), Image.Resampling.LANCZOS)
        canvas = Image.new("RGB", (794, 559), "#efeae7")
        canvas.paste(proof, ((794 - proof.width) // 2, (559 - proof.height) // 2))
        canvas.save(source.with_name(source.stem + "-print.png"), dpi=(96, 96))
