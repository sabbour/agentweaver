import importlib.util
import sys
from pathlib import Path
from PIL import Image, ImageOps, ImageDraw

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("author", HERE / "author-eight.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
stage = sys.argv[1]
names = list(module.MODELS)
for batch in range(2):
    sheet = Image.new("RGB", (1654, 1206), "#efeae7")
    for i, name in enumerate(names[batch*4:batch*4+4]):
        image = Image.open(module.REVIEWS / name / f"{name}-{stage}.png").convert("RGB")
        print_image = ImageOps.contain(image, (827, 583))
        x, y = (i%2)*827, (i//2)*603
        sheet.paste(print_image, (x+(827-print_image.width)//2, y+(583-print_image.height)//2))
        ImageDraw.Draw(sheet).text((x+10,y+584), name, fill="#272320")
    sheet.save(HERE / f"{stage}-print-sheet-{batch+1}.png")
