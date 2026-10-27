"""Compare the actual explicit rule set against both native reverse indexes.
Usage: python relations_integration.py RAWDATA NATIVE_APP DOTNET_CLI_DLL
"""
import json
from pathlib import Path
import re
import subprocess
import sys
import tempfile

raw, app, cli = sys.argv[1:]
root = Path(__file__).resolve().parents[1]
rules = json.loads(subprocess.check_output(["node", "-e", "global.window={};require(process.argv[1]);console.log(JSON.stringify(window.CONFIG_RELATION_RULES))", str(root / "Resources/relationships.js")], text=True))
cases = [
    ("AdPolicyConf", "id", "1"), ("ActivityConf", "id", "700"),
    ("DailyMissionItemConf", "id", "1"), ("DailyMissionActionConf", "type", "LvlFinishSuper"),
    ("MilestoneTypeConf", "target_type", "2000"), ("GoodsConf", "id", "103"),
    ("GoodsConf", "key", "gold"), ("LangConf", "key", "ui_milestone_redblock_desc"),
    ("JourneyBackgroundConf", "id", "1"),
]


def values(value, mode):
    if mode == "scalar":
        return [str(value)]
    if mode == "list":
        return [str(item) for item in value]
    if mode in ("rewardKeys", "journeyBackgroundIds"):
        result = []
        for text in value if isinstance(value, list) else [value]:
            for line in text.splitlines():
                match = re.fullmatch(r"\s*(\w+)\s*\+\s*(\d+)\s*([kmhi]?)\s*", line)
                if match:
                    if mode == "rewardKeys":
                        result.append(match[1])
                    elif match[1] == "journey_background" and match[3] == "i":
                        result.append(match[2])
        return result
    raise AssertionError(mode)


with tempfile.TemporaryDirectory(prefix="pairpair-relations-") as temporary:
    for target, target_key, value in cases:
        applicable = [rule for rule in rules if target in rule["targets"] and rule["targetKey"].lower() == target_key.lower()]
        expected = set()
        for rule in applicable:
            for source in rule.get("sources", []):
                path = Path(raw) / (source + ".json")
                if not path.exists():
                    continue
                document = json.loads(path.read_text())
                sheet, table = next((key, table) for key, table in document.items() if key.endswith(("Map", "Slice")))
                records = [table[key] for key in sorted(table)] if isinstance(table, dict) else table
                for index, row in enumerate(records, 3):
                    for field in rule["fields"]:
                        if field in row and value in values(row[field], rule["mode"]):
                            expected.add((source + ".json::" + sheet, index, field))
        assert expected, (target, target_key, value)
        rules_path = Path(temporary) / "rules.json"
        rules_path.write_text(json.dumps(applicable))
        for command in ([app], ["dotnet", cli]):
            result = json.loads(subprocess.check_output(command + ["--reverse-rules-audit", raw, value, target, str(rules_path)], text=True))
            references = result if isinstance(result, list) else result["references"]
            actual = {(item["bookId"], item["row"], item["field"]) for item in references}
            assert actual == expected, (command[0], target, target_key, "missing", expected - actual, "extra", actual - expected)
        print(f"{target}.{target_key}={value}: {len(expected)} references, native/windows=ok", flush=True)
