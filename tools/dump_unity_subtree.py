"""Dump a Unity scene GameObject subtree as readable text.

Walks the YAML documents of a .unity scene from a named root object, printing
each GameObject's RectTransform + components (Image, TMP text, layout groups,
custom scripts) with their interesting serialized fields, and resolving asset
GUIDs to project paths via .meta files.

Usage:
  python tools/dump_unity_subtree.py <scene.unity> <RootObjectName> <out.txt> [<assets_dir>]
"""
import os
import re
import sys
import json

DOC_RE = re.compile(r"^--- !u!(\d+) &(\d+)")
GUID_RE = re.compile(r"guid: ([0-9a-f]{32})")

KNOWN_SCRIPTS = {
    "fe87c0e1cc204ed48ad3b37840f39efc": "Image",
    "f4688fdb7df04437aeb418b961361dc5": "TextMeshProUGUI",
    "59f8146938fff824cb5fd77236b75775": "VerticalLayoutGroup",
    "30649d3a9faa99c48a7b1166b86bf2a0": "HorizontalLayoutGroup",
    "8a8695521f0d02e499659fee002a26c2": "GridLayoutGroup",
    "3245ec927659c4140ac4f8d17403cc18": "ContentSizeFitter",
    "306cc8c2b49d7114eaa3623786fc2126": "LayoutElement",
    "4e29b1a8efbd4b44bb3f3716e73f07ff": "Button",
    "1aa08ab6e0800fa44ae55d278d1423e3": "CanvasScaler",
    "dc42784cf147c0c48a680349fa168899": "GraphicRaycaster",
}

# Boilerplate keys not worth printing
NOISE = {
    "m_ObjectHideFlags", "m_CorrespondingSourceObject", "m_PrefabInstance",
    "m_PrefabAsset", "m_GameObject", "m_EditorHideFlags", "m_EditorClassIdentifier",
    "m_Script", "m_OnCullStateChanged", "m_Maskable", "m_RaycastTarget",
    "m_RaycastPadding", "m_CullTransparentMesh", "serializedVersion",
    "m_Navigation", "m_Transition", "m_Colors", "m_SpriteState", "m_AnimationTriggers",
    "m_isWaitingOnResourceLoad", "m_ignoreActiveState", "m_baseMaterial",
    "m_StaticEditorFlags", "m_Icon", "m_NavMeshLayer", "m_TagString", "m_Layer",
}


def parse_scene(path):
    docs = {}
    cur_id, cur = None, None
    with open(path, "r", encoding="utf-8", errors="replace") as f:
        for line in f:
            m = DOC_RE.match(line)
            if m:
                if cur_id is not None:
                    docs[cur_id] = cur
                cur_id = int(m.group(2))
                cur = {"class": int(m.group(1)), "lines": []}
            elif cur is not None:
                cur["lines"].append(line.rstrip("\n"))
    if cur_id is not None:
        docs[cur_id] = cur
    return docs


def top_level_blocks(lines):
    """Split component body into (key, [lines]) for 2-space-indented top keys."""
    blocks = []
    key, block = None, []
    for ln in lines[1:]:  # skip the 'ClassName:' first line
        m = re.match(r"^  (\w[\w ]*):(.*)$", ln)
        if m and not ln.startswith("   "):
            if key is not None:
                blocks.append((key, block))
            key, block = m.group(1), [ln]
        elif key is not None:
            block.append(ln)
    if key is not None:
        blocks.append((key, block))
    return blocks


def field(lines, name):
    for ln in lines:
        m = re.match(rf"^\s*{re.escape(name)}: (.*)$", ln)
        if m:
            return m.group(1).strip()
    return None


def list_fileids(lines, after_key):
    """Collect '- {fileID: N}' or '- component: {fileID: N}' entries under a key."""
    out, active = [], False
    for ln in lines:
        if re.match(rf"^  {re.escape(after_key)}:", ln):
            active = True
            continue
        if active:
            m = re.match(r"^  - (?:component: )?{fileID: (\d+)}", ln)
            if m:
                out.append(int(m.group(1)))
            elif not ln.startswith("  - ") and not ln.startswith("    "):
                break
    return out


def build_guid_index(assets_dir, needed, cache_path):
    cache = {}
    if os.path.exists(cache_path):
        with open(cache_path) as f:
            cache = json.load(f)
    missing = {g for g in needed if g not in cache}
    if missing:
        print(f"resolving {len(missing)} guids by scanning .meta files...")
        for root, dirs, files in os.walk(assets_dir):
            for fn in files:
                if not fn.endswith(".meta"):
                    continue
                p = os.path.join(root, fn)
                try:
                    with open(p, "r", encoding="utf-8", errors="replace") as f:
                        head = f.read(300)
                    m = GUID_RE.search(head)
                    if m and m.group(1) in missing:
                        cache[m.group(1)] = os.path.relpath(p[:-5], assets_dir)
                        missing.discard(m.group(1))
                        if not missing:
                            break
                except OSError:
                    continue
            if not missing:
                break
        with open(cache_path, "w") as f:
            json.dump(cache, f, indent=1)
    return cache


def main():
    scene_path, root_name, out_path = sys.argv[1], sys.argv[2], sys.argv[3]
    assets_dir = sys.argv[4] if len(sys.argv) > 4 else os.path.join(
        os.path.dirname(os.path.dirname(scene_path)), "Assets")
    # default assets_dir guess: scene at Assets/.../x.unity -> walk up to Assets
    d = os.path.dirname(scene_path)
    while d and os.path.basename(d) != "Assets":
        d = os.path.dirname(d)
    if d:
        assets_dir = d

    docs = parse_scene(scene_path)
    print(f"parsed {len(docs)} documents")

    # Find root GameObject by name
    root_go = None
    for fid, doc in docs.items():
        if doc["class"] == 1 and field(doc["lines"], "m_Name") == root_name:
            root_go = fid
            break
    if root_go is None:
        print(f"ERROR: GameObject '{root_name}' not found")
        sys.exit(1)

    # GameObject fid -> its RectTransform fid (class 224 or 4)
    def go_transform(go_fid):
        for cfid in list_fileids(docs[go_fid]["lines"], "m_Component"):
            if cfid in docs and docs[cfid]["class"] in (224, 4):
                return cfid
        return None

    out, guids_needed = [], set()

    def emit(s):
        out.append(s)

    def dump_component(cfid, indent):
        doc = docs.get(cfid)
        if doc is None:
            return
        cls = doc["class"]
        lines = doc["lines"]
        pad = "  " * indent
        if cls == 222:  # CanvasRenderer
            return
        if cls == 224:  # RectTransform
            rot = field(lines, "m_LocalRotation")
            scale = field(lines, "m_LocalScale")
            emit(f"{pad}RectTransform: anchorMin={field(lines,'m_AnchorMin')} anchorMax={field(lines,'m_AnchorMax')} "
                 f"pos={field(lines,'m_AnchoredPosition')} size={field(lines,'m_SizeDelta')} pivot={field(lines,'m_Pivot')}")
            if rot and "z: 0" not in rot.replace("z: -0,", "z: 0"):
                emit(f"{pad}  rotation={rot}")
            if scale and not ("x: 1," in scale and "y: 1," in scale):
                emit(f"{pad}  scale={scale}")
            return
        if cls == 114:
            sg = field(lines, "m_Script")
            guid = GUID_RE.search(sg or "")
            gid = guid.group(1) if guid else "?"
            name = KNOWN_SCRIPTS.get(gid)
            if name is None:
                guids_needed.add(gid)
                name = f"Script[{gid}]"
            emit(f"{pad}{name}:")
            for key, block in top_level_blocks(lines):
                if key in NOISE:
                    continue
                for g in GUID_RE.finditer("\n".join(block)):
                    guids_needed.add(g.group(1))
                if len(block) == 1:
                    val = block[0].split(":", 1)[1].strip()
                    if val in ("", "[]", "{}"):
                        continue
                    emit(f"{pad}  {key}: {val}")
                else:
                    for bl in block:
                        emit(f"{pad}  {bl.strip() if bl.strip().startswith('-') else bl[2:]}")
            return
        # other classes: brief
        emit(f"{pad}[class {cls}]")

    def walk(go_fid, indent):
        go = docs[go_fid]
        name = field(go["lines"], "m_Name")
        active = field(go["lines"], "m_IsActive")
        pad = "  " * indent
        emit(f"{pad}GO '{name}'{' (INACTIVE)' if active == '0' else ''}  [go {go_fid}]")
        tfid = go_transform(go_fid)
        for cfid in list_fileids(go["lines"], "m_Component"):
            dump_component(cfid, indent + 1)
        if tfid:
            for child_t in list_fileids(docs[tfid]["lines"], "m_Children"):
                if child_t in docs:
                    cgo = field(docs[child_t]["lines"], "m_GameObject")
                    m = re.search(r"fileID: (\d+)", cgo or "")
                    if m and int(m.group(1)) in docs:
                        walk(int(m.group(1)), indent + 1)

    walk(root_go, 0)

    # Resolve guids
    cache_path = os.path.join(os.path.dirname(out_path), "unity_guid_cache.json")
    index = build_guid_index(assets_dir, guids_needed, cache_path)
    text = "\n".join(out)
    for gid, path in index.items():
        text = text.replace(gid, f"{gid} <{path}>")

    with open(out_path, "w", encoding="utf-8") as f:
        f.write(text + "\n")
    unresolved = {g for g in guids_needed if g not in index}
    print(f"wrote {out_path} ({len(out)} lines), {len(unresolved)} unresolved guids")
    if unresolved:
        print("unresolved:", *sorted(unresolved))


if __name__ == "__main__":
    main()
