"""Prints one Markdown table across spike runs. Usage: compare.py <run directory>..."""
import json
import pathlib
import sys

import report

runs = {pathlib.Path(d).name: json.loads((pathlib.Path(d) / "result.json").read_text()) for d in sys.argv[1:]}
print(report.compare(runs), end="")
