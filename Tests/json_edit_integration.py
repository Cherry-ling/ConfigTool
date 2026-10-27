"""Verify production-format copies using the independent official protobuf decoder.

Usage: python json_edit_integration.py RAWDATA HELPER [HELPER_ARGS...]
Requires the Python protobuf package. Never writes to the supplied project.
"""
import base64
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile

from google.protobuf import descriptor_pb2, descriptor_pool, json_format, message_factory


source = Path(sys.argv[1]).resolve()
helper = sys.argv[2:]
generated = source.parents[2] / "Scripts/Game/GameHotfix/GameProto/Runtime/gen/rawdata"
pool = descriptor_pool.DescriptorPool()
pending = []
for path in generated.glob("*.cs"):
    match = re.search(r"byte\[\] descriptorData\s*=.*?string\.Concat\((.*?)\)\);", path.read_text(), re.S)
    if match:
        data = base64.b64decode("".join(re.findall(r'"([A-Za-z0-9+/=]+)"', match[1])))
        pending.append(descriptor_pb2.FileDescriptorProto.FromString(data))
while pending:
    progress = False
    for item in pending[:]:
        try:
            pool.Add(item)
        except TypeError:
            continue
        pending.remove(item)
        progress = True
    assert progress, [item.name for item in pending]


def varint(data, offset):
    value = 0
    for shift in range(0, 70, 7):
        byte = data[offset]
        offset += 1
        value |= (byte & 127) << shift
        if byte < 128:
            return value, offset
    raise AssertionError("Invalid varint")


def without_field(data, field_number):
    result = bytearray()
    offset = 0
    while offset < len(data):
        start = offset
        tag, offset = varint(data, offset)
        kind = tag & 7
        if kind == 0:
            _, offset = varint(data, offset)
        elif kind == 2:
            length, offset = varint(data, offset)
            offset += length
        else:
            offset += {1: 8, 5: 4}[kind]
        if tag >> 3 != field_number:
            result.extend(data[start:offset])
    return bytes(result)


def verify(path):
    document = json.loads(path.read_text())
    message_type = message_factory.GetMessageClass(pool.FindMessageTypeByName("rawdata." + path.stem))
    expected = json_format.ParseDict(document, message_type())
    data = path.with_suffix(".bytes").read_bytes()
    actual = message_type.FromString(data)
    assert actual == expected, path.name
    hash_key = next(key for key in document if key.lower() == "md5")
    hash_number = actual.DESCRIPTOR.fields_by_name[hash_key].number
    assert document[hash_key] == hashlib.md5(without_field(data, hash_number)).hexdigest(), path.name


with tempfile.TemporaryDirectory(prefix="pairpair-json-edit-") as temporary:
    assets = Path(temporary) / "Assets"
    raw = assets / "Res/Config/rawdata"
    raw.mkdir(parents=True)
    schema = assets / "Scripts/Game/GameHotfix/GameProto/Runtime/gen/rawdata"
    schema.mkdir(parents=True)
    for path in generated.glob("*.cs"):
        shutil.copy2(path, schema / path.name)
    for pattern in ("*.json", "*.bytes"):
        for path in source.glob(pattern):
            shutil.copy2(path, raw / path.name)

    def request(name, changes=(), *, original=None, binary=None, fail=False, conflict=False):
        path = raw / (name + ".json")
        before_json = path.read_bytes()
        before_bytes = path.with_suffix(".bytes").read_bytes()
        document = json.loads(before_json)
        sheet = next(key for key in document if key.endswith(("Map", "Slice")))
        payload = {
            "path": str(path), "sheetName": sheet,
            "original": base64.b64encode(before_json if original is None else original).decode(),
            "originalBinary": base64.b64encode(before_bytes if binary is None else binary).decode(),
            "changes": list(changes),
        }
        result = subprocess.run(helper, input=json.dumps(payload), text=True, capture_output=True)
        if fail:
            assert result.returncode != 0, (name, result.stdout, result.stderr)
            if conflict:
                assert result.returncode == 3, result.stderr
            assert path.read_bytes() == before_json and path.with_suffix(".bytes").read_bytes() == before_bytes
        else:
            assert result.returncode == 0, (name, result.stderr)
            verify(path)
        return path

    def change(name, key, field, value):
        document = json.loads((raw / (name + ".json")).read_text())
        table = next(value for key, value in document.items() if key.endswith(("Map", "Slice")))
        records = [table[key] for key in sorted(table)] if isinstance(table, dict) else table
        row = sorted(table).index(str(key)) if isinstance(table, dict) else int(key)
        fields = sorted(set().union(*(record.keys() for record in records)), key=lambda field: (field.lower() not in ("id", "key"), field))
        return {"row": row + 3, "column": fields.index(field) + 1, "value": value}

    for path in sorted(raw.glob("*.json")):
        before = json.loads(path.read_text())
        request(path.stem)
        after = json.loads(path.read_text())
        assert {key: value for key, value in before.items() if key.lower() != "md5"} == {key: value for key, value in after.items() if key.lower() != "md5"}
    print("all_tables_roundtrip=ok tables=" + str(len(list(raw.glob('*.json')))), flush=True)

    name = "ActivityConf"
    request(name, [change(name, "100", field, value) for field, value in [
        ("unlock_level", "88"), ("cycle_start_time", "8.5"), ("enable", "false"),
        ("name", '编辑测试 "quoted"\n第二行'), ("reward_rate", "[1,5,10]")]])
    edited = json.loads((raw / (name + ".json")).read_text())["ActivityMap"]["100"]
    assert edited["unlock_level"] == 88 and edited["enable"] is False and edited["cycle_start_time"] == 8.5
    assert edited["name"] == '编辑测试 "quoted"\n第二行' and edited["reward_rate"] == [1, 5, 10]
    for field, value in [("enable", "1"), ("unlock_level", "2147483648"), ("reward_rate", '["bad"]'), ("reward_rate", "[invalid]"), ("id", "101")]:
        request(name, [change(name, "100", field, value)], fail=True)
    request(name, [{"row": 3, "column": 0, "value": "bad"}], fail=True)
    request(name, original=b"stale", fail=True, conflict=True)
    request(name, binary=b"stale", fail=True, conflict=True)
    request(name, [change(name, "100", "id", "1100")])
    assert json.loads((raw / (name + ".json")).read_text())["ActivityMap"]["1100"]["id"] == 1100
    request("LangConf", [change("LangConf", "__CHECK__", "key", "__EDIT_CHECK__")])
    assert "__EDIT_CHECK__" in json.loads((raw / "LangConf.json").read_text())["LangMap"]
    request("MiscConf", [change("MiscConf", 0, "PubgHighPlayerWeight", '[{"Value":[8,20]}]'), change("MiscConf", 0, "BinInitAmount", "3")])
    assert json.loads((raw / "MiscConf.json").read_text())["MiscSlice"][0]["PubgHighPlayerWeight"] == [{"Value": [8, 20]}]
    request("MiscConf", [change("MiscConf", 0, "PubgHighPlayerWeight", '[{"Value":[8,"bad"]}]')], fail=True)
    print("typed_edits=ok map_rekey=ok nested_arrays=ok unicode=ok invalid_input_preserves_pair=ok json_and_bytes_conflicts=ok", flush=True)
