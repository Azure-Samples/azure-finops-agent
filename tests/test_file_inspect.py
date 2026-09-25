import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

import pandas as pd
from openpyxl import Workbook

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "src/Dashboard/AI/Tools/Resources/file_inspect.py"
SPEC = importlib.util.spec_from_file_location("file_inspect", SCRIPT)
HELPER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(HELPER)


class FileQueryTests(unittest.TestCase):
    def setUp(self):
        self.frame = pd.DataFrame([
            {"month": "Jan", "category": "AI", "owner": "A", "cost": 10},
            {"month": "Jan", "category": "AI", "owner": "B", "cost": 20},
            {"month": "Feb", "category": "AI", "owner": None, "cost": 30},
            {"month": "Feb", "category": "VM", "owner": "A", "cost": 40},
        ])

    def test_filtered_multicolumn_totals(self):
        result = HELPER._df_query(self.frame, {
            "mode": "query", "filters": [{"column": "category", "op": "eq", "value": "AI"}],
            "group_by": ["month", "category"], "aggregates": [{"column": "cost", "op": "sum", "as": "total"}],
            "sort": [{"column": "month", "direction": "asc"}],
        }, "csv")
        self.assertTrue(result["ok"])
        self.assertTrue(result["complete"])
        self.assertEqual(result["source_rows"], 4)
        self.assertEqual(result["filtered_rows"], 3)
        self.assertEqual(result["totals"]["total"], 60)
        self.assertEqual(sum(row["total"] for row in result["rows"]), 60)

    def test_null_groups_and_truncation_keep_totals(self):
        result = HELPER._df_query(self.frame, {"mode": "aggregate", "group_by": ["owner", "month"], "column": "cost", "agg": "sum", "limit": 1}, "csv")
        self.assertTrue(result["ok"])
        self.assertFalse(result["complete"])
        self.assertEqual(result["total_results"], 4)
        self.assertEqual(result["totals"]["sum"], 100)

    def test_contains_is_literal_not_regex(self):
        result = HELPER._df_query(self.frame, {"mode": "filter", "column": "owner", "op": "contains", "value": ".*"}, "csv")
        self.assertEqual(result["total_matches"], 0)

    def test_projection_follows_filter_sort_and_pagination(self):
        result = HELPER._df_query(self.frame, {
            "mode": "query", "filters": [{"column": "category", "op": "eq", "value": "AI"}],
            "sort": [{"column": "cost", "direction": "desc"}],
            "columns": ["owner"], "offset": 1, "limit": 1,
        }, "csv")
        self.assertTrue(result["ok"])
        self.assertEqual(result["rows"], [{"owner": "B"}])
        self.assertEqual(result["source_rows"], 4)
        self.assertEqual(result["filtered_rows"], 3)
        self.assertEqual(result["total_results"], 3)
        self.assertFalse(result["complete"])

    def test_projection_preserves_full_aggregate_totals(self):
        result = HELPER._df_query(self.frame, {
            "mode": "query", "group_by": ["month"],
            "aggregates": [{"column": "cost", "op": "sum", "as": "total"}],
            "columns": ["month", "total"], "limit": 1,
        }, "csv")
        self.assertTrue(result["ok"])
        self.assertEqual(result["rows"], [{"month": "Feb", "total": 70}])
        self.assertEqual(result["totals"]["total"], 100)
        self.assertEqual(result["total_results"], 2)
        self.assertFalse(result["complete"])

    def test_invalid_projection_is_rejected(self):
        for columns in ([], ["cost", "cost"], ["unknown"], "cost", [1], [["cost"]], [str(index) for index in range(51)]):
            with self.subTest(columns=columns):
                result = HELPER._df_query(self.frame, {"mode": "query", "columns": columns}, "csv")
                self.assertFalse(result["ok"])

    def test_invalid_numeric_is_unknown_not_zero(self):
        result = HELPER._df_query(pd.DataFrame({"cost": ["unknown"]}), {"mode": "aggregate", "column": "cost", "agg": "sum"}, "csv")
        self.assertIsNone(HELPER._clean(result)["value"])
        self.assertEqual(result["invalid_numeric"]["sum"], 1)

    def test_arbitrary_aggregate_is_rejected(self):
        result = HELPER._df_query(self.frame, {"mode": "aggregate", "column": "cost", "agg": "to_pickle"}, "csv")
        self.assertFalse(result["ok"])

    def test_json_selector_is_not_filesystem_path(self):
        result = HELPER._handle_json({"mode": "json_path", "path": "/host-only/upload", "jsonPath": "properties.rows[0].cost"}, b'{"properties":{"rows":[{"cost":42.73}]}}')
        self.assertEqual(result["value"], 42.73)
        malformed = HELPER._handle_json({"mode": "json_path", "jsonPath": "rows[-1]"}, b'{"rows":[1]}')
        self.assertFalse(malformed["ok"])

    def test_xlsx_uses_same_query_contract(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "test.xlsx"
            workbook = Workbook()
            sheet = workbook.active
            sheet.append(list(self.frame.columns))
            for row in self.frame.itertuples(index=False, name=None):
                sheet.append(row)
            workbook.save(path)
            result = HELPER._handle_xlsx({"mode": "aggregate", "group_by": ["month", "category"], "column": "cost", "agg": "sum", "filters": [{"column": "category", "op": "eq", "value": "AI"}]}, str(path))
            self.assertTrue(result["ok"])
            self.assertEqual(result["totals"]["sum"], 60)
            projected = HELPER._handle_xlsx({"mode": "query", "columns": ["cost"], "limit": 1}, str(path))
            self.assertTrue(projected["ok"])
            self.assertEqual(projected["rows"], [{"cost": 10}])
            self.assertEqual(projected["source_rows"], 4)
            self.assertFalse(projected["complete"])

    def test_outside_root_and_missing_root_fail_closed(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "allowed"
            root.mkdir()
            outside = Path(directory) / "outside.json"
            outside.write_text('{"private":"synthetic"}', encoding="utf-8")
            request = json.dumps({"mode": "preview", "path": str(outside), "kind": "json"})
            for configured in (str(root), ""):
                result = subprocess.run([sys.executable, str(SCRIPT)], input=request, text=True, capture_output=True, env={**os.environ, "FINOPS_UPLOAD_ROOT": configured}, check=True)
                parsed = json.loads(result.stdout)
                self.assertFalse(parsed["ok"])
                self.assertNotIn(str(outside), result.stdout)
                self.assertNotIn("synthetic", result.stdout)


if __name__ == "__main__":
    unittest.main()