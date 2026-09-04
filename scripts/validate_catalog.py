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
        assert len(item["sources"]) >= 2
        for source in item["sources"]:
            parsed = urllib.parse.urlparse(source["url"])
            assert parsed.scheme == "https"
        assert pathlib.PurePosixPath(item["path"]).name == item["path"]
    for bundle in model.get("downloadBundles", []):
        assert bundle["size"] > 0
        assert len(bundle["sha256"]) == 64
        int(bundle["sha256"], 16)
        assert bundle["archive"] in {"tar.bz2", "zip"}
        assert bundle["sources"]
        for source in bundle["sources"]:
            assert urllib.parse.urlparse(source["url"]).scheme == "https"

for role in ("TXT_ASR", "SRT_ASR"):
    mapped = {}
    for profile in ("Fast", "Balanced", "High Accuracy"):
        matches = [model["id"] for model in catalog["models"]
                   if model["enabled"] and "windows-x64" in model["platform"]
                   and role in model["role"]
                   and profile in model.get("qualityProfiles", {}).get(role, [])]
        assert len(matches) == 1, (role, profile, matches)
        mapped[profile] = matches[0]
    assert len(set(mapped.values())) == 3, (role, mapped)
assert "base" not in mapped["High Accuracy"].lower()

for profile in ("Fast", "Balanced", "High Accuracy"):
    selected = {}
    for role in ("TXT_ASR", "SRT_ASR"):
        selected[role] = next(model for model in catalog["models"]
                              if model["enabled"] and "windows-x64" in model["platform"]
                              and role in model["role"]
                              and profile in model.get("qualityProfiles", {}).get(role, []))
    txt = selected["TXT_ASR"]
    srt = selected["SRT_ASR"]
    assert "Timestamp" not in txt.get("roleDisplayNames", {}).get("TXT_ASR", txt["displayName"])
    assert srt.get("roleRuntimeOptions", {}).get("SRT_ASR", {}).get("timestampMode") is True
    assert txt.get("roleRuntimeOptions", {}).get("TXT_ASR", {}).get("timestampMode") is False
    if txt["id"] == srt["id"]:
        assert txt["roleDisplayNames"]["TXT_ASR"] != srt["roleDisplayNames"]["SRT_ASR"]
print(f"catalog ok: {len(ids)} models")
