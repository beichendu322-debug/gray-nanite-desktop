# Gray Nanite asset guide

## Historical visual baseline

The palette, rendering preferences, pose, and prompt choices recorded here describe earlier approved versions and generation intent. They are historical references, not mandatory aesthetic rules for future agents. Future proposals may change color, style, composition, or pose according to the user's current request; the previous agent's taste is not authoritative. Canvas, alpha, hit-region, and layout assumptions are separate technical concerns that can evolve with the implementation and its tests. See [WORK-NOTES.md](../WORK-NOTES.md) and [NEXT-AGENT-PROMPT.md](../NEXT-AGENT-PROMPT.md).

- The earlier canvas used near-black graphite, cool cyan for information/interaction, and amber for warnings; the commander used restrained crimson and antique gold ceremonial trim.
- The earlier environment favored matte steel, thin technical lines, restrained glow, and generous negative space behind the character.
- The approved 2D anime commander had clean linework, controlled shading, silver hair, a black/gold/crimson officer uniform and ceremonial sword. Earlier prompts retained the then-confirmed proportions and raised-hand hologram pose, and avoided a plastic-skin appearance and generated typography.
- Earlier guidance favored vector/CSS/WPF geometry for UI chrome, and raster assets for character art and environment atmosphere; the current application is WPF.

## Files

- `character-neutral.png`, `character-happy.png`, `character-analyzing.png`, `character-alert.png`: unchanged legacy aliases of the approved neutral anime commander. The current runtime uses neutral plus the separately approved expression sprites below; these aliases remain for compatibility and identity validation.
- `character-expression-{smile,laugh,surprised,angry,pout,shy,focused,wry}.png`: eight approved transparent 1024×1536/96-DPI expression variants, copied byte-for-byte from `output/imagegen/gray-expression-previews-20261013-v1`. Cached and embedded in the executable. They keep the same pose/canvas; existing layout and animation transforms are not reset on a source swap. Bounds may vary by at most three source pixels. These are approved image-generated variants, not pixel-identical face-only edits.
- `character.png`: neutral alias used by older builds.
- `character-original.png`: original reference kept for comparison.
- `background-command.png`: wide dark-steel command chamber plate with center negative space.
- `startup-voice.wav`: the user's supplied 2026-10-13 English startup recording, decoded from MP3 into mono 22050 Hz 16-bit PCM for native WPF playback. Original speed, pitch, spoken content and pauses are retained; duration is 7.0269387755 seconds. This external asset is copied alongside the executable, not embedded into the character resources.
- `nanite-icons.svg`: original vector marks for the seven folders, inbox, recycle, Codex, battery, storage, settings, search, and refresh.
- `character-voice/ahoge-01.mp3` through `ahoge-03.mp3`: the three supplied antenna/ahoge responses.
- `character-voice/body-01.mp3` through `body-03.mp3`: the three supplied general-body responses.
- `character-voice/chest-01.mp3` through `chest-03.mp3`: the three supplied uniform/chest responses. All nine MP3s retain the original bytes, speed and pitch; they are external publish assets, independent of the startup recording and QQ Music.

## Generation notes

2026-10-13 expression integration: each clip carries an explicit expression. Ahoge 01/02/03 uses surprised/angry/pout; body 01/02/03 uses laugh/smile/wry; chest 01/02/03 uses shy/angry/wry. Ambient happy/analyzing/alert uses smile/focused/angry, otherwise the original neutral. A spoken reaction takes priority until audio ends, fails, or is stopped by hide/exit; then the latest ambient expression is restored. A mood update during playback does not overwrite the reaction. Both foreground and holographic ghost switch together. No new movement, voice transformation, QQ Music setting, file binding, or startup timing is introduced.

The character prompts anchor every variation to the same reference image and explicitly request natural anatomy, restrained painterly shading, clean alpha edges, and no text/logos/watermarks. The environment prompt requests a quiet center, denser outer steel structures, and only cyan/amber instrumentation points.

2026-10-12: the built-in image-generation editor removed the background from the previously approved anime illustration without requesting a redesign. The 1024×1536 PNG retains real alpha (including soft hologram/hair edges) and full-body framing. The detailed edit prompt and provenance are saved with the workspace cutout at `output/imagegen/gray-commander-approved-transparent.prompt.md`. No position, breathing, projection, startup-voice, or file-binding settings were changed for this replacement.

2026-10-12 follow-up: a single silver anime ahoge was added with the built-in image editor, preserving the approved uniform, pose, proportions and hologram. The transparent sprite and final prompt are saved as `output/imagegen/gray-commander-ahoge-transparent.png` and `.prompt.md`. All five runtime aliases use this sprite. The entire projection stage moved another 20 DIP left with the same available width, and its right clipping boundary now includes the reserved gap so animated hair is not cut off; dialogue and file-panel bounds remain protected. Adaptive image/ghost top headroom keeps the ahoge inside the header boundary; when necessary, Image's Uniform fit makes a small size adjustment (about 1.5% at the current 2560×1392 work area).

The UI palette/layout was informed by public visual references emphasizing dark full-bleed canvases, thin neon borders, visible grid scaffolding, and amber reserved for warnings:

- https://chrislemke.github.io/website_designs/examples/Cyberpunk.html
- https://freeui.net/style/sci-fi/
