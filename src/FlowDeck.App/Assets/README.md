# FlowDeck icon

Generated with the built-in image generation tool on 2026-09-25; enlarged with the same tool on 2026-10-10 and recolored first to lime green, then to neutral white at the user's request on 2026-10-11.

- `FlowDeck.png`: enlarged fan artwork with a neutral white/silver ring and hub and transparent outer corners.
- `FlowDeck-preview.png`: 256 px preview.
- `FlowDeck.ico`: 32-bit RGBA images at 16, 20, 24, 32, 40, 48, 64, 128 and 256 px. Used by the executable, WPF window and notification area.

The ICO frames and preview were resized and encoded with System.Drawing; the generated artwork was otherwise preserved.

The 2026-10-10 revision increases the bright ring's horizontal span from 80.1% to 90.1% of the canvas (about 12% larger). The WPF workspace windows load the ICO with `BitmapFrame.Create` so Windows can select from all nine sizes instead of scaling the first 16 px frame.

When packaging this change, update both the EXE's application icon and the DLL's WPF resource. Reusing an old EXE while replacing only the DLL leaves Explorer's icon unchanged. Desktop shortcuts targeting `Start-FlowDeck.cmd` need an explicit `IconLocation`. Point it to the active GUI package's `FlowDeck.exe,0` when deploying a new icon so the shortcut and taskbar use the same verified artwork and Windows receives a new icon cache key. Preserve the shortcut's other fields and notify the shell of that shortcut's update.

## White recolor edit prompt

Built-in image generation edit, using the enlarged lime-green `FlowDeck.png` as its target. The five silver-white blades, enlarged composition, graphite tile, top ring gap, and transparent exterior are retained. The ring and hub are now neutral white/silver. The generated PNG alpha is preserved; System.Drawing only resizes and encodes the preview and nine ICO frames.

```text
Use case: precise-object-edit
Asset type: FlowDeck Windows application icon, exact white/neutral recolor of the provided edit target.
Input image: existing enlarged lime-green FlowDeck.png is the sole edit target.
Primary request: Change the lime/yellow-green luminous outer ring and circular central hub to clean neutral white/silver, with white highlights #FFFFFF, off-white midtones #F4F4F4 and neutral silver-gray shading #BDBDBD. Remove all green, yellow, purple or other chromatic accent tint from those parts. The ring and hub must read as WHITE, not dark gray.
Preserve exactly the existing enlarged composition, fan geometry, all five silver-white sweeping blades and their outline/position, dark graphite rounded-square tile, black interior, top 12 o'clock ring gap, ring thickness, central hub size, soft satin lighting and overall scale. Keep the ring close to the tile edge as it currently is. Preserve the actual transparent alpha around the outer rounded corners. The finished icon must be the same original design with only its lime palette changed to neutral white, crisp and legible for a Windows taskbar and desktop at 16/20/24/32/48/256 px. No geometry changes, no additional blades, no text, no new symbols, no extra shadow, no border, no margin increase, no background outside the tile. Produce a square high-resolution transparent PNG.
```

## Earlier lime-green recolor edit prompt

Built-in image generation edit, using the enlarged `FlowDeck.png` as its target. The five silver-white blades, enlarged composition, graphite tile, top ring gap, and transparent exterior are retained. Only the ring and hub palette changes from purple to lime green. The generated PNG alpha is preserved; System.Drawing only resizes and encodes the preview and nine ICO frames.

```text
Use case: precise-object-edit
Asset type: FlowDeck Windows application icon, exact recolor of the provided edit target.
Input image: existing enlarged FlowDeck.png is the sole edit target.
Primary request: Change only the lavender/purple/violet luminous outer ring and circular central hub to a vivid yellow-green lime palette, with midtone #B6E544, pale lime highlights #E4F6AE and deeper lime-green shading #76A923.
Preserve exactly the existing enlarged composition, fan geometry, five silver-white sweeping blades, each blade's position and outline, dark graphite rounded-square tile, black interior, top 12 o'clock ring gap, ring thickness, central hub size, soft satin lighting, and overall scale. Keep the ring close to the tile edge as it currently is. Preserve actual transparent alpha around the outer rounded corners. The result must look like the same original icon with only the purple palette replaced by lime green. No shape changes, no additional blade, no text, no new symbol, no extra shadows, no border, no margin increase, no background outside the tile. Produce a square high-resolution transparent PNG.
```

## Enlargement edit prompt

Built-in image generation edit, using the previous `FlowDeck.png` as its target:

```text
Make the existing fan symbol larger within the same square canvas for the Windows taskbar.
Preserve the five silver-white sweeping blades, lavender hub and ring, graphite rounded-square tile,
12 o'clock ring gap, original palette, and transparent outer corners. Enlarge the entire ring and fan
together, with the ring close to the tile edge and only a slim dark inset. Keep the ring fully visible
and centered, with crisp shapes readable at 16/24/32 px. No new symbols, text, detail, or extra shadow.
```

## Generation prompt

```text
Use case: logo-brand
Asset type: Windows desktop application icon for "FlowDeck", a local PC cooling fan control, temperature monitoring and RGB lighting utility.
Primary request: Create one polished, distinctive app icon, a stylized five-blade PC cooling fan centered inside a thick luminous lavender control ring, on a dark graphite rounded-square tile.
Style/medium: Premium modern Windows app icon, precise simple geometry, subtly dimensional satin surfaces with restrained soft highlights. Crisp vector-like silhouettes, very few details. Strong recognizability at 16 and 32 pixels.
Composition: Single icon only, straight-on perfectly centered, square 1024x1024 canvas. Tile occupies 92 percent of canvas, softly rounded corners. The fan and ring occupy about 75 percent of tile. Five broad sweeping white/silver fan blades around a solid small lavender circular central hub, generously separated dark gaps. Broad outer lavender ring with a single clean short gap at the very top suggesting a control dial, but no extra symbols. Preserve overall rotational balance.
Color palette: graphite #202127 tile, dark interior #14151A, silver-white #F0EEF8 blades, lavender #B394F5 and violet #8058D3 ring. Gentle lavender gradient only on the thick ring. No rainbow.
Scene/backdrop: Actual transparent alpha outside the rounded square tile; tile itself opaque. No surrounding shadow or scenery.
Constraints: No text, no letters, no numbers, no watermark, no wordmark, no tiny ticks, no screws, no grid, no border decorations. Not a screenshot or presentation; deliver exactly one isolated production icon. Clean edges with no excessive bloom or blur.
```
