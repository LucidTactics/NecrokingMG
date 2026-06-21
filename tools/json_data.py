#!/usr/bin/env python3
"""Manipulate Necroking data JSON files that hold a list of id-keyed structs.

These files look like:

    { "spells": [ { "id": "fireball", ... }, { "id": "frostbolt", ... } ] }

i.e. a top-level object with ONE key whose value is a list of structs, and every
struct carries a unique string "id". (A bare top-level list also works.) This tool
edits that list in place while preserving the file's formatting (2-space indent,
non-ASCII escaped the way the game writes it).

Operations
----------
  list      (no extra args)
        Print every struct's id, one per line. (No write.)

  read      <id>
        Print the struct with that id as JSON. (No write.)

  duplicate <new_id> [key=value ...]
        Copy the target struct, give the copy <new_id>, apply the overrides, and
        append it. Errors if <new_id> already exists.

  delete    (no extra args)
        Remove the struct with the target id.

  create    <id> [key=value ...]
        (target_id is the new id.) Make a fresh struct {"id": <id>, ...overrides}.
        Errors if the id already exists.

  update    [key=value ...]
        Update the target struct's keys. Value parsing (see below) decides type.
        key=null  DELETES the key.   key="null" sets the string "null".

Value parsing (for key=value pairs)
------------------------------------
  null            -> delete the key (in create, a null key is simply not set)
  "null"          -> the string "null"
  true / false    -> boolean
  12  / 3.14      -> number (int / float)
  "anything"      -> the string anything (explicit quotes strip & force string)
  foo             -> the string foo
  [..] / {..}     -> parsed as JSON (lists / nested objects)

A value that is valid JSON (e.g. {"r":1}) is parsed as JSON; otherwise it is a
bare string. Wrap in double quotes to force a string ("12" -> "12" not 12).

Usage
-----
  python tools/json_data.py <file.json> <op> [target_id] [args...]

Examples
  python tools/json_data.py data/spells.json list
  python tools/json_data.py data/spells.json read fireball
  python tools/json_data.py data/spells.json duplicate fireball icebolt name="Ice Bolt" damage=12 primaryPath=water
  python tools/json_data.py data/spells.json update fireball manaCost=3 hidden=true
  python tools/json_data.py data/spells.json update fireball secondaryPath=null
  python tools/json_data.py data/potions.json delete potion_frenzy
  python tools/json_data.py data/buffs.json create buff_custom name="Custom" duration=5
"""
import json
import sys

_SENTINEL_DELETE = object()


def _parse_value(raw):
    """Parse a CLI value into a Python value. Returns _SENTINEL_DELETE for null."""
    if raw == "null":
        return _SENTINEL_DELETE
    # Explicitly-quoted string: strip the quotes, force string (no further parsing).
    if len(raw) >= 2 and raw[0] == '"' and raw[-1] == '"':
        return raw[1:-1]
    # Try JSON: handles true/false, numbers, lists, objects, and "quoted strings".
    try:
        return json.loads(raw)
    except (ValueError, json.JSONDecodeError):
        return raw  # bare string


def _parse_kv_args(args):
    """Parse ['k=v', ...] into a dict. Value 'null' maps to _SENTINEL_DELETE."""
    out = {}
    for a in args:
        if "=" not in a:
            _die(f"argument '{a}' is not a key=value pair")
        key, raw = a.split("=", 1)
        if not key:
            _die(f"argument '{a}' has an empty key")
        out[key] = _parse_value(raw)
    return out


def _die(msg):
    print(f"ERROR: {msg}", file=sys.stderr)
    sys.exit(1)


def _load(path):
    try:
        with open(path, "r", encoding="utf-8") as f:
            return json.load(f)
    except FileNotFoundError:
        _die(f"file not found: {path}")
    except json.JSONDecodeError as e:
        _die(f"{path} is not valid JSON: {e}")


def _find_list(root, path):
    """Return (list, container, key) for the struct list, validating the format.

    Format requirement: a top-level list, OR a top-level object with exactly one
    key whose value is a list. Every element must be a dict with a unique 'id'.
    """
    if isinstance(root, list):
        lst, container, key = root, None, None
    elif isinstance(root, dict):
        list_keys = [k for k, v in root.items() if isinstance(v, list)]
        if len(list_keys) != 1:
            _die(
                f"{path}: expected a top-level object with exactly one list value "
                f"(found list-valued keys: {list_keys or 'none'})"
            )
        key = list_keys[0]
        lst, container = root[key], root
    else:
        _die(f"{path}: top-level JSON must be an object or a list")

    seen = set()
    for i, el in enumerate(lst):
        if not isinstance(el, dict):
            _die(f"{path}: element {i} is not an object")
        if "id" not in el:
            _die(f"{path}: element {i} has no 'id' field")
        eid = el["id"]
        if eid in seen:
            _die(f"{path}: duplicate id '{eid}' — file is not uniquely id-keyed")
        seen.add(eid)
    return lst, container, key


def _index_of(lst, target_id):
    for i, el in enumerate(lst):
        if el.get("id") == target_id:
            return i
    return -1


def _save(path, root):
    with open(path, "w", encoding="utf-8") as f:
        # ensure_ascii=True + no trailing newline matches how the game serializes
        # (e.g. it escapes '+' as + and writes no final '\n').
        json.dump(root, f, indent=2, ensure_ascii=True)


def _apply_overrides(struct, overrides):
    for k, v in overrides.items():
        if v is _SENTINEL_DELETE:
            struct.pop(k, None)
        else:
            struct[k] = v


def main(argv):
    if len(argv) < 2:
        print(__doc__)
        _die("need at least <file.json> <op>")

    path = argv[0]
    op = argv[1].lower()
    rest = argv[2:]

    root = _load(path)
    lst, container, _key = _find_list(root, path)

    if op == "list":
        for el in lst:
            print(el["id"])
        return

    if op == "read":
        if not rest:
            _die("read needs <target_id>")
        idx = _index_of(lst, rest[0])
        if idx < 0:
            _die(f"no struct with id '{rest[0]}'")
        print(json.dumps(lst[idx], indent=2, ensure_ascii=False))
        return

    if op == "duplicate":
        if len(rest) < 2:
            _die("duplicate needs <target_id> <new_id> [key=value ...]")
        target_id, new_id = rest[0], rest[1]
        overrides = _parse_kv_args(rest[2:])
        src = _index_of(lst, target_id)
        if src < 0:
            _die(f"no struct with id '{target_id}' to duplicate")
        if _index_of(lst, new_id) >= 0:
            _die(f"id '{new_id}' already exists")
        import copy
        clone = copy.deepcopy(lst[src])
        clone["id"] = new_id
        _apply_overrides(clone, overrides)
        lst.append(clone)
        _save(path, root)
        print(f"OK: duplicated '{target_id}' -> '{new_id}' ({len(lst)} structs)")
        return

    if op == "delete":
        if not rest:
            _die("delete needs <target_id>")
        idx = _index_of(lst, rest[0])
        if idx < 0:
            _die(f"no struct with id '{rest[0]}'")
        del lst[idx]
        _save(path, root)
        print(f"OK: deleted '{rest[0]}' ({len(lst)} structs)")
        return

    if op == "create":
        if not rest:
            _die("create needs <new_id> [key=value ...]")
        new_id = rest[0]
        overrides = _parse_kv_args(rest[1:])
        if _index_of(lst, new_id) >= 0:
            _die(f"id '{new_id}' already exists")
        struct = {"id": new_id}
        _apply_overrides(struct, overrides)
        lst.append(struct)
        _save(path, root)
        print(f"OK: created '{new_id}' ({len(lst)} structs)")
        return

    if op == "update":
        if not rest:
            _die("update needs <target_id> [key=value ...]")
        target_id = rest[0]
        overrides = _parse_kv_args(rest[1:])
        idx = _index_of(lst, target_id)
        if idx < 0:
            _die(f"no struct with id '{target_id}'")
        if "id" in overrides:
            _die("cannot change 'id' via update (use duplicate + delete)")
        _apply_overrides(lst[idx], overrides)
        _save(path, root)
        print(f"OK: updated '{target_id}' ({len(overrides)} keys)")
        return

    _die(f"unknown operation '{op}' (list|read|duplicate|delete|create|update)")


if __name__ == "__main__":
    main(sys.argv[1:])
