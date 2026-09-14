import sys
from pathlib import Path
from PIL import Image, ImageOps

for argument in sys.argv[1:]:
    path = Path(argument)
    with Image.open(path) as im:
        proof = ImageOps.contain(im.convert("RGB"), (794, 559), Image.Resampling.LANCZOS)
        page = Image.new("RGB", (794, 559), "#efeae7")
        page.paste(proof, ((794-proof.width)//2, (559-proof.height)//2))
        page.save(path.with_name(path.stem+"-print.png"), dpi=(96,96))
