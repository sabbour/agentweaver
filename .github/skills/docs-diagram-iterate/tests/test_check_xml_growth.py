import importlib.util
import tempfile
import unittest
from pathlib import Path


SCRIPT = Path(__file__).parents[1] / "scripts" / "check_xml_growth.py"
SPEC = importlib.util.spec_from_file_location("check_xml_growth", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader
SPEC.loader.exec_module(MODULE)


def vertex(cell_id, x, y, value="A", style="rounded=1;fillColor=#fdfbf8;"):
    return (
        f'<mxCell id="{cell_id}" value="{value}" style="{style}" vertex="1" parent="1">'
        f'<mxGeometry x="{x}" y="{y}" width="40" height="30" as="geometry"/>'
        "</mxCell>"
    )


def diagram(cells: str, comment: str = "", metadata: str = "") -> str:
    return (
        f'<mxfile {metadata}>{comment}<diagram id="page">'
        '<mxGraphModel page="1" pageWidth="1112" pageHeight="789"><root>'
        f'<mxCell id="0"/><mxCell id="1" parent="0"/>{cells}'
        "</root></mxGraphModel></diagram></mxfile>"
    )


class XmlGrowthTests(unittest.TestCase):
    def test_excludes_comments_embedded_images_and_unapproved_metadata(self):
        base = diagram(vertex("2", 10, 10))
        padded = diagram(
            vertex(
                "2",
                10,
                10,
                style="rounded=1;fillColor=#fdfbf8;image=data:image/png;base64," + ("A" * 10000),
            ),
            comment="<!-- " + ("padding " * 100) + " -->",
        )
        self.assertLess(MODULE.meaningful_count(padded) - MODULE.meaningful_count(base), 32)

    def test_detects_invisible_vertex_padding(self):
        xml = diagram(vertex("hidden", 10, 10, value="padding", style="opacity=0;"))
        self.assertEqual(["hidden"], MODULE.analyze(xml)["invisible_cells"])

    def test_rejects_off_page_cell(self):
        report = MODULE.assess(
            diagram(vertex("base", 10, 10)),
            diagram(vertex("base", 10, 10) + vertex("far", 99999, 10)),
            minimum_ratio=1,
        )
        self.assertIn("far", report["off_page_cells"])
        self.assertFalse(report["passed"])

    def test_print_scale_defines_the_single_page_coordinate_bounds(self):
        xml = diagram(vertex("inside", 1500, 100) + vertex("outside", 2230, 100))
        xml = xml.replace('page="1"', 'page="1" pageScale="2"')
        report = MODULE.analyze(xml)
        self.assertEqual(["outside"], report["off_page_cells"])
        self.assertEqual(2224, report["page_width"])

    def test_rejects_invalid_print_scale(self):
        for scale in ("0", "-1", "nan", "inf", "invalid"):
            with self.subTest(scale=scale), self.assertRaises(ValueError):
                MODULE.analyze(diagram(vertex("one", 10, 10)).replace(
                    'page="1"', f'page="1" pageScale="{scale}"'))

    def test_rejects_duplicate_cells(self):
        duplicate = (
            vertex("one", 10, 10, value="same")
            + vertex("two", 10, 10, value="same")
        )
        report = MODULE.assess(diagram(vertex("base", 10, 10)), diagram(duplicate), minimum_ratio=1)
        self.assertTrue(report["duplicate_cells"])
        self.assertFalse(report["passed"])

    def test_rejects_metadata_padding(self):
        padded = diagram(vertex("one", 10, 10), metadata='data-padding="' + ("x" * 200) + '"')
        report = MODULE.assess(diagram(vertex("base", 10, 10)), padded, minimum_ratio=1)
        self.assertTrue(report["metadata_padding"])
        self.assertFalse(report["passed"])

    def test_accepts_ninefold_visible_semantic_growth(self):
        pitch = diagram(vertex("base", 10, 10, value="Component"))
        cells = "".join(
            vertex(f"v{index}", 10 + (index % 4) * 60, 10 + (index // 4) * 50, value="Component")
            for index in range(12)
        )
        report = MODULE.assess(pitch, diagram(cells))
        self.assertGreaterEqual(report["growth_ratio"], 9)
        self.assertTrue(report["passed"])

    def test_requires_uncompressed_single_page_with_bounds(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "compressed.drawio"
            path.write_text('<mxfile><diagram id="page">compressed</diagram></mxfile>', encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "uncompressed"):
                MODULE.load_uncompressed_xml(path)


if __name__ == "__main__":
    unittest.main()
