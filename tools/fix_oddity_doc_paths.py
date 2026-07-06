# One-off: update doc references after the 2026-07-06 "known oddities" refactor
# (GameSystems/ folder dissolved into Game/, UnitSystem.cs -> UnitModel.cs,
#  player UI classes moved from Game//Render//Editor/ into UI/).
import pathlib

ROOT = pathlib.Path(__file__).resolve().parent.parent
REPLACEMENTS = [
    ("UnitSystem.cs", "UnitModel.cs"),
    ("GameSystems/DeathFogSystem", "Game/DeathFogSystem"),
    ("GameSystems/JumpSystem", "Game/JumpSystem"),
    ("GameSystems/TrampleSystem", "Game/TrampleSystem"),
    ("GameSystems/WeaponBonusEffect", "Game/WeaponBonusEffect"),
    ("Editor/MultiplayerWindow", "UI/MultiplayerWindow"),
    ("Render/HUDRenderer", "UI/HUDRenderer"),
    ("Render/CharacterStatsUI", "UI/CharacterStatsUI"),
    ("Game/InventoryUI", "UI/InventoryUI"),
    ("Game/BuildingMenuUI", "UI/BuildingMenuUI"),
    ("Game/CraftingMenuUI", "UI/CraftingMenuUI"),
    ("Game/TableCraftMenuUI", "UI/TableCraftMenuUI"),
]

targets = list((ROOT / "docs").rglob("*.md")) + list((ROOT / "todos").glob("*.md"))
targets += [ROOT / "CLAUDE.md", ROOT / "Necroking" / "Net" / "README.md"]

for path in targets:
    text = orig = path.read_text(encoding="utf-8")
    for old, new in REPLACEMENTS:
        text = text.replace(old, new)
    if text != orig:
        with path.open("w", encoding="utf-8", newline="") as f:
            f.write(text)
        print(f"updated {path.relative_to(ROOT)}")
