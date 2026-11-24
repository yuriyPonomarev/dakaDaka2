# Dark and Darker reference decoder

Read-only tools that turn the local extracted game packages into JSON so system design can be checked against
decoded facts (AGENTS.md, "Required Dark and Darker reference"). Standard-library Python 3.10, no downloads.
The game executable is only read; nothing is run, injected or patched. Decoded output is game-derived: keep it
outside the repository and never copy assets from it into the project.

## Rebuild

```powershell
$tools = "Tools/DarkerReference"
$out   = "D:/DarkAndDarker-decoded"
python -I $tools/cityhash.py
python -I $tools/reflect_db.py "C:/Program Files (x86)/Steam/steamapps/common/Dark and Darker/DungeonCrawler/Binaries/Win64/DungeonCrawler.exe" "$out/schema_steam_0.18.161.json"
python -I $tools/reflect_db.py "C:/DarkAndDarkerTest/Dark and Darker Test/DungeonCrawler/Binaries/Win64/DungeonCrawler.exe" "$out/schema_test_0.17.153.json"
$schemas = "$out/schema_steam_0.18.161.json;$out/schema_test_0.17.153.json"
$root = "C:/Users/yrapa/Downloads/DarkAndDarker-extracted"
$c = "pakchunk500-Windows/DungeonCrawler/Content/DungeonCrawler"
python -I $tools/zen_decode.py $schemas $out $root "$c/Data/Generated/V2/**/*.uheader" "$c/Item/**/AM_*.uheader" "$c/Item/**/GA_*.uheader" "$c/Projectile/**/*.uheader" "$c/Data/GameplayAbility/**/*.uheader"
python -I $tools/weapon_sheet.py $out ArmingSword RecurveBow
python -I $tools/montage_summary.py "$out/$c/Item/Weapon/ArmingSword"
```

`zen_decode.py <schemas> - <file.uheader>` prints one package. Patch chunks (`*_0_P`) override base chunks; decode
the patch path when a file exists in both.

## How it works

- Cooked properties are "unversioned": a header of (skip, count) fragments selects indices in the class's property
  list, values follow without names. Order: the class's own properties, then each parent's.
- `reflect_db.py` finds every `FClassParams` in the executable, reads its property array, names the class from the
  UTF-16 literals of its registration function and links the parent through the dependency singleton. Structs and
  enums are resolved through the function pointers stored in the property parameters.
- Exports name their class by a hash of `/Script/<Module>.<Class>` (CityHash64, `cityhash.py`).
- A Blueprint default object is decoded with the first native class in its parent chain (followed across packages).

## Limits

- The extraction was cooked by a build between the two installed executables. A table can therefore fail with both
  schemas; the decoder reports it (`error` / `tail`) instead of guessing. After a game update rebuild the schema
  from the new executable and keep the older one in the list.
- Blueprint graphs, native code, Blueprint-added variables, C++ constructor defaults and animation tracks are not
  decoded. Montage notifies (events with times) are.
- An export is trusted only when its bytes are consumed exactly. Sub-objects usually end with `tail 4`; their
  listed properties are still correct, but check a surprising value against a second asset.
