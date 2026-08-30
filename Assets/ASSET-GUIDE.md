# Gray Nanite asset guide

## Visual contract

- Near-black graphite canvas; cool cyan is information/interaction, amber is warning only; the commander uses restrained crimson and antique gold ceremonial trim.
- Matte steel, thin technical lines, restrained glow, generous negative space behind the character.
- Human-proportioned anime-influenced character art: believable hands, subtle facial asymmetry, controlled shading, no plastic skin or random typography.
- UI chrome is vector/CSS/WPF geometry; raster assets are reserved for character art and environment atmosphere.

## Files

- `character-neutral.png`, `character-happy.png`, `character-analyzing.png`, `character-alert.png`: same commander identity, four runtime moods.
- `character.png`: neutral alias used by older builds.
- `character-original.png`: original reference kept for comparison.
- `background-command.png`: wide dark-steel command chamber plate with center negative space.
- `nanite-icons.svg`: original vector marks for the seven folders, inbox, recycle, Codex, battery, storage, settings, search, and refresh.

## Generation notes

The character prompts anchor every variation to the same reference image and explicitly request natural anatomy, restrained painterly shading, clean alpha edges, and no text/logos/watermarks. The environment prompt requests a quiet center, denser outer steel structures, and only cyan/amber instrumentation points.

The UI palette/layout was informed by public visual references emphasizing dark full-bleed canvases, thin neon borders, visible grid scaffolding, and amber reserved for warnings:

- https://chrislemke.github.io/website_designs/examples/Cyberpunk.html
- https://freeui.net/style/sci-fi/
