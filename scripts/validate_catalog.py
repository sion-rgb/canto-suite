import hashlib
import json
import pathlib
import sys
import urllib.parse

root = pathlib.Path(__file__).resolve().parents[1]
catalog = json.loads((root / "shared/model-catalog/catalog.v1.json").read_text(encoding="utf-8"))
assert catalog["schemaVersion"] == 1
ids = set()
for model in catalog["models"]:
    assert model["id"] not in ids
    ids.add(model["id"])
    if not model["enabled"]:
        assert model.get("disabledReason")
    for item in model["files"]:
        assert item["size"] > 0
        assert len(item["sha256"]) == 64
        int(item["sha256"], 16)
        parsed = urllib.parse.urlparse(item["url"])
        assert parsed.scheme == "https"
        assert pathlib.PurePosixPath(item["path"]).name == item["path"]
print(f"catalog ok: {len(ids)} models")
