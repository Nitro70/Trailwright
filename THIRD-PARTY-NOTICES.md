# Third-party notices

## three.js (r170)

`src/Trailwright.App/wwwroot/vendor/three.module.min.js` and `OrbitControls.js` are from
[three.js](https://github.com/mrdoob/three.js), MIT License, Copyright (c) 2010-2024 three.js authors.
The full licence text is in `src/Trailwright.App/wwwroot/vendor/three-LICENSE.txt`.

## Smaz codebook

`src/Trailwright.Core/Data/smaz_codebook.json` is the codebook of
[Smaz](https://github.com/antirez/smaz) by Salvatore Sanfilippo (BSD 3-Clause License), as used by
the SmazSharp port that Trailmakers ships. It was read back out of the game so the byte values match
exactly.

## Trailmakers data

`src/Trailwright.Core/Data/catalog.json` lists facts about the game's blocks (names, ids, grid sizes,
attachment points, setting ranges and default settings) so blueprints can be read and written. It is
generated from a local game install by `tools/re/make_catalog.py`. Trailmakers is made by Flashbulb
Games; Trailwright is an unofficial fan tool and is not affiliated with or endorsed by them. No game
code, models, textures or sounds are included.
