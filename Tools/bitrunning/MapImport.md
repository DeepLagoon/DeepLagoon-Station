# Bitrunning map import

The importer reproduces the 26 WhiteMoon domain grids and 14 safehouse grids from the source commit recorded in map_manifest.json. It requires Python 3.11 or newer, Pillow and PyYAML. Use a Git checkout that contains the source commit. The importer reads DMM, DM and DMI blobs from Git.

    py -3 Tools/bitrunning/import_maps.py C:\old_disk\repos\ss13\WhiteMoon-Station
    py -3 Tools/bitrunning/import_maps.py C:\old_disk\repos\ss13\WhiteMoon-Station --verify

Every generated map records its source path, digest and dimensions. The manifest lists the encountered source atom paths and their target tiles, entities, decals or map structures. An empty target list identifies a source atom without a native mapping. An unavailable prototype is recorded by name. The source maps retain BYOND-specific area metadata and atom fields for further work.

The importer joins the domains to their authored safehouses using the modular anchor and connector. For a safehouse with no connector the anchor goes at the lower-left cell. Transparent template cells retain their original floor. The exported safehouse files are standalone grids for mapping and verification. Virtual grids keep their disconnected floor islands together, with grid splitting disabled.

The grids preserve the main routes and scenario locations using native DeepLagoon assets. Specific adaptations include a linked gateway for the bluespace peel in Clown Planet, EVA supplies for Syndicate Assault and Vaporwave, destructible resin and mining tools in Xeno Nest, and a catwalk to the Blood Drunk Miner fight. The glacier uses passable stair tiles and hazardous cliff edges in the single-level grid. Ancient Milsim retains its yellow and blue keycard doors. Psyker Zombies has native firearm supply crates at its mystery box locations, and Pipedream uses native straight and bent disposal pipes. Dark areas and scenario progression are configured by the bitrunning runtime.

## WhiteMoon textures

The importer resolves each placed object's DMM sprite overrides and its inherited WhiteMoon DM icon properties in project include order. It uses the runtime side of `MAP_SWITCH`. It exports the original frames, directional states and delays from DMI into RSI. Generated entities inherit the port's existing prototypes and localization. Floor variants inherit their native tile's physical properties. The generated map appearances are local to Bitrunning.

The map art pack restores visible objects omitted in the initial conversion, including embedded and standalone copies of safehouses. Objects without native gameplay equivalents appear as scenery or can be picked up as ordinary items.

The [art manifest](../../Resources/Textures/_DeepLagoon/Bitrunning/WhiteMoon/art_manifest.json) lists the source, state and target of each export. Animated floor textures use native anchored sprites that disappear when their physical tile changes. Native interactions continue to drive the open, closed, damaged and powered states. Where WhiteMoon builds an appearance from an outfit, picks random art in DM or changes color and overlays at runtime, the generated assets reuse the available source states with DeepLagoon's appearance logic. Runtime equipment, avatar and encounter assets from the separate sprite importer have their own [manifest](../../Resources/Textures/_DeepLagoon/Bitrunning/source_states.json).
