import base64
import importlib.util
import io
from pathlib import Path
import unittest
import zipfile
from openpyxl import load_workbook

SCRIPT = Path(__file__).resolve().parents[1] / "src/backend/AI/Tools/Resources/report_export.py"
SPEC = importlib.util.spec_from_file_location("report_export", SCRIPT)
REPORT = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(REPORT)


class ReportTests(unittest.TestCase):
    def request(self, format_name):
        return {"format": format_name, "data": {"title": "Synthetic report", "source": "Synthetic test data", "sheets": [{"name": "Costs", "columns": ["name", "cost"], "rows": [["=1+1", 42.73], ["<script>bad()</script>", 10]], "sourceRowCount": 2}]}}

    def test_xlsx_preserves_numbers_and_neutralizes_formulas(self):
        result = REPORT.render(self.request("xlsx"))
        workbook = load_workbook(io.BytesIO(base64.b64decode(result["contentBase64"])))
        self.assertEqual(workbook.active["A2"].data_type, "s")
        self.assertEqual(workbook.active["A2"].value, "'=1+1")
        self.assertEqual(workbook.active["B2"].value, 42.73)
        self.assertEqual(workbook.active.max_row, 3)

    def test_csv_has_safe_formula_cells(self):
        content = base64.b64decode(REPORT.render(self.request("csv"))["contentBase64"]).decode("utf-8-sig")
        self.assertIn("'=1+1", content)
        self.assertIn("42.73", content)

    def test_html_escapes_data_and_has_filter(self):
        content = base64.b64decode(REPORT.render(self.request("html"))["contentBase64"]).decode("utf-8")
        self.assertNotIn("<script>bad()</script>", content)
        self.assertIn("&lt;script&gt;", content)
        self.assertIn('id="filter"', content)

    def test_declared_coverage_must_match_delivered_rows(self):
        request = self.request("html")
        request["data"]["sheets"][0]["sourceRowCount"] = 113
        with self.assertRaisesRegex(ValueError, r"Sheet 1 \(Costs\): 2 rows were supplied but sourceRowCount says 113"):
            REPORT.render(request)

    def test_mismatch_tells_the_model_how_to_resolve_it(self):
        request = self.request("html")
        request["data"]["sheets"][0]["sourceRowCount"] = 3
        with self.assertRaisesRegex(ValueError, r"leave sourceRowCount out.*supply every one of the 3 rows"):
            REPORT.render(request)

    def test_authored_text_needs_no_source_row_count(self):
        request = self.request("html")
        del request["data"]["sheets"][0]["sourceRowCount"]
        self.assertEqual(REPORT.render(request)["rows"], 2)

    def test_errors_name_the_sheet_and_row_to_fix(self):
        request = self.request("xlsx")
        request["data"]["sheets"].append({"name": "Disks", "columns": ["name", "size", "tier"], "rows": [["a", 1, "P10"], ["b", 2]], "sourceRowCount": 2})
        with self.assertRaisesRegex(ValueError, r"Sheet 2 \(Disks\): row 2 has 2 values but the sheet has 3 columns"):
            REPORT.render(request)
        request["data"]["sheets"][1]["rows"][1] = ["b", {"nested": True}, "P10"]
        with self.assertRaisesRegex(ValueError, r"Sheet 2 \(Disks\): row 2: Cells must be scalar values"):
            REPORT.render(request)

    def charted(self, format_name, chart_type="bar"):
        request = self.request(format_name)
        sheet = request["data"]["sheets"][0]
        sheet["text"] = "Spend fell in March.\nRight-size the largest VM first."
        sheet["chart"] = {"type": chart_type, "label": "name", "values": ["cost"], "title": "Cost by <name>"}
        return request

    def test_html_draws_the_chart_as_static_svg_from_the_sheet_rows(self):
        for chart_type in ("bar", "horizontal_bar", "line", "pie"):
            content = base64.b64decode(REPORT.render(self.charted("html", chart_type))["contentBase64"]).decode("utf-8")
            figure = content[content.index("<figure>"):content.index("</figure>")]
            self.assertIn("<svg", figure)
            self.assertIn("Cost by &lt;name&gt;", figure)
            self.assertIn("&lt;script&gt;bad()&lt;/script&gt;", figure)
            self.assertNotIn("<script", figure)
            self.assertNotIn("href", figure)
            self.assertIn('<p class="text">Right-size the largest VM first.</p>', content)
        self.assertEqual(content.count("<script"), 2)

    def test_chart_errors_name_the_sheet_and_never_echo_values(self):
        request = self.charted("html")
        request["data"]["sheets"][0]["chart"]["values"] = ["missing"]
        with self.assertRaisesRegex(ValueError, r"Sheet 1 \(Costs\): chart values must name 1 to 4 other columns"):
            REPORT.render(request)
        request = self.charted("html")
        request["data"]["sheets"][0]["chart"]["values"] = ["name"]
        request["data"]["sheets"][0]["chart"]["label"] = "cost"
        with self.assertRaises(ValueError) as error:
            REPORT.render(request)
        self.assertIn("Sheet 1 (Costs): row 1: chart values must be numbers", str(error.exception))
        self.assertNotIn("=1+1", str(error.exception))

    def test_xlsx_adds_an_excel_chart_and_a_notes_sheet(self):
        content = base64.b64decode(REPORT.render(self.charted("xlsx"))["contentBase64"])
        with zipfile.ZipFile(io.BytesIO(content)) as archive:
            self.assertTrue(any(name.startswith("xl/charts/chart") for name in archive.namelist()))
        workbook = load_workbook(io.BytesIO(content))
        self.assertEqual(workbook["Costs"].max_row, 3)
        self.assertEqual(workbook["Notes"]["A2"].value, "Costs")
        self.assertIn("Right-size the largest VM first.", workbook["Notes"]["B2"].value)

    def test_chart_numbers_read_naturally(self):
        self.assertEqual(REPORT.number_text(26192.55), "26.2K")
        self.assertEqual(REPORT.number_text(10000), "10K")
        self.assertEqual(REPORT.number_text(1640.25), "1,640.25")
        self.assertEqual(REPORT.number_text(-150.0), "-150")
        self.assertEqual(REPORT.number_text(2.5e6), "2.5M")
        self.assertEqual(REPORT.tick_texts([-2000, 0, 2000, 4000, 6000, 8000, 10000]), ["-2K", "0", "2K", "4K", "6K", "8K", "10K"])
        self.assertEqual(REPORT.tick_texts([0, 0.5, 1]), ["0", "0.5", "1"])

    def test_csv_rejects_text_and_charts(self):
        with self.assertRaisesRegex(ValueError, r"CSV holds one table only"):
            REPORT.render(self.charted("csv"))


if __name__ == "__main__":
    unittest.main()