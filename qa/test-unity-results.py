#!/usr/bin/env python3
import contextlib
import importlib.util
import io
import pathlib
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("unity_results", pathlib.Path(__file__).with_name("unity-results.py"))
results = importlib.util.module_from_spec(spec)
spec.loader.exec_module(results)


class UnityResultsTests(unittest.TestCase):
    def run_report(self, states, *, filtered=False, total=None, root_result="Passed"):
        passed = states.count("Passed")
        failed = states.count("Failed")
        xml = f'<test-run total="{len(states) if total is None else total}" passed="{passed}" failed="{failed}" result="{root_result}">'
        xml += "".join(f'<test-case result="{state}" name="test-{i}" />' for i, state in enumerate(states))
        xml += '</test-run>'
        with tempfile.TemporaryDirectory() as directory:
            path = pathlib.Path(directory) / "results.xml"
            path.write_text(xml)
            with contextlib.redirect_stdout(io.StringIO()):
                return results.report(path, "EditMode", 3, "SelectedFixture" if filtered else "")

    def test_filtered_pass_does_not_require_full_suite(self):
        self.assertEqual(0, self.run_report(["Passed"], filtered=True))

    def test_same_result_is_not_full_acceptance(self):
        self.assertEqual(1, self.run_report(["Passed"]))

    def test_full_suite_accepts_reported_skip(self):
        self.assertEqual(0, self.run_report(["Passed", "Passed", "Skipped"]))

    def test_empty_filter_match_is_failure(self):
        self.assertEqual(1, self.run_report([], filtered=True))

    def test_all_skipped_is_not_verification(self):
        self.assertEqual(1, self.run_report(["Skipped"], filtered=True))

    def test_failed_case_rejects_filtered_run(self):
        self.assertEqual(1, self.run_report(["Passed", "Failed"], filtered=True))

    def test_missing_cases_cannot_satisfy_floor(self):
        self.assertEqual(1, self.run_report(["Passed"], total=3))

    def test_failed_setup_rejects_run(self):
        self.assertEqual(1, self.run_report(["Passed"], filtered=True, root_result="Failed"))


if __name__ == "__main__":
    unittest.main()
