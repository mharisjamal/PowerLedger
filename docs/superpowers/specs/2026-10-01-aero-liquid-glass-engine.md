# Aero liquid glass engine (2026-10-01)

Part 1 of 2 of the owner's liquid glass for the Aero look: the engine. It covers the live backdrop, the filter chain
and the one element the look puts behind each glass piece. Part 2 (`plan-lg/look`, merged into `plan-lg/engine`) is
the look: styles, edges, the drop shadow, the highlights and which pieces are glass.

## Source of truth

The source is `polidario/Frontend-Projects`, `liquid-glass-vue/src/components/AppCard.vue`. It has no licence, so the
engine is reimplemented from its parameters and no code is copied. Its glass:

| Part | Value |
|---|---|
| backdrop-filter | `brightness(1.1) blur(2px) url(#displacementFilter)`, in that order |
| url filter | `feTurbulence type=turbulence baseFrequency=0.01 numOctaves=2` (seed 0, no stitching), into `feDisplacementMap in=SourceGraphic in2=turbulence scale=200 xChannelSelector=R yChannelSelector=G` |
| color-interpolation-filters | the default, linearRGB |
| filter | `drop-shadow(-8px -10px 46px #0000005f)` |
| radius | 28 px, no tint |
| ::before | `inset 6px 6px 0 -6px rgba(255,255,255,.7), inset 0 0 8px 1px rgba(255,255,255,.7)` |

`LiquidGlassRecipe` holds every one of these as a constant. CSS pixels are WPF units.

## API for the look

Everything is in `PowerLedger.App.Aero` (folder `src/PowerLedger.App/Aero/LiquidGlass`) and is internal, like the
rest of Aero.

### `LiquidGlassBackdrop : FrameworkElement`

Put it as the bottom layer of a glass piece, filling the piece. It shows the filtered live backdrop within the piece's
rounded rectangle and nothing else: no tint, shadow, rim or highlight. It isn't hit-testable or focusable.

| Property | Type | Default | Meaning |
|---|---|---|---|
| `CornerRadius` | `CornerRadius` | 28 | the piece's corners |
| `Brightness` | `double` | 1.1 | the first filter |
| `BlurDeviation` | `double` | 2 | the second filter: the Gaussian's standard deviation, in units |
| `Scale` | `double` | 200 | the third filter: the displacement's scale, in units (0 turns it off) |
| `IsLive` | `bool` | true | false draws nothing and lets the capture go (reduce transparency, a hidden piece) |
| `Kind` (read only) | `LiquidGlassSourceKind` | `None` | `Live`, `Wallpaper` (the fallback), `Inside` (a piece inside another) or `None` |
| `InBackdrop` (attached) | `bool` | true | false leaves an element out of what a piece inside another reads (the look sets it on a piece's shadow and glow) |

```xml
<Grid>
    <aero:LiquidGlassBackdrop CornerRadius="{TemplateBinding CornerRadius}" />
    <!-- the look's shadow, tint, glow and content above -->
</Grid>
```

It works in any top-level window: the Aero window, the watts overlay (a layered topmost window), popups and menus. The
first piece that loads in a window leaves that window out of capture and starts its source. The last one to unload
stops it.

**Glass inside glass.** The element a backdrop is the bottom layer of is the glass piece, CSS's element with the
backdrop-filter. A backdrop whose element sits inside another piece's element is nested (`IsNested`): as in Chromium,
where every backdrop-filter element is a backdrop root, it reads only what the outer element painted before it (the
outer tint and the content beneath it), never the screen and not the outer glass. The ring's glass hole bends the ring,
a bubble on a well reads the well's tint, a bubble on bare glass reads nothing and the outer glass shows through it.
Pieces side by side, or over one another in one element (a dialog over a page), are not nested.

**The constraint for the look.** On the main path (composed, below), the glass lies beneath the whole window's WPF
content and the element itself draws nothing. Whatever the look draws over a piece shows over the glass exactly as it
did over the element: the shadow, the tint, the glow and the content. Anything opaque over a piece hides it, as it would
in the browser. That includes a pane background, a dense well, a dialog's own frost, or a window background behind the
region.

### Switches

| Name | What it does |
|---|---|
| `GlassSettings.ShowInScreenshots` (Settings, off by default, anything but JSON `true` reads as off) | copied by `GlassMaterial` into `LiquidGlassSources.AllowScreenshots` |
| `LiquidGlassSources.AllowScreenshots` | true: every glass window goes to `WDA_NONE`, the live capture stops, and the glass shows the wallpaper through the same recipe; false restores both, at once |
| `LiquidGlassSources.ExcludeFromCapture` | the harness's switch, apart from the owner's: false leaves windows capturable for on-screen checks |
| `LiquidGlassSources.GpuAllowed` | the tests' switch: false keeps every window on the WPF effects path |
| `LiquidGlassSources.Override` | `Func<HwndSource, ILiquidGlassSource>`: hands every window a fake source, so nothing captures the screen |
| `LiquidGlassGovernor.Budget` | the CPU safety net, 8 % of one core for the whole process by default |
| `LiquidGlassSources.Animate(ms)` | AeroMotion calls it for each animation: composed glass follows its piece every frame until then |

### `ILiquidGlassSource`

The picture behind one top-level window, for the WPF effects path: `Kind`, `Image` (an `ImageSource`), `ScreenBounds`
(physical pixels on the virtual screen) and `Changed`. On the GPU path, `WindowGlassSource.Gpu` carries the window's
`GpuGlassWindow` instead.

## What Chromium does, measured

These were measured against headless Edge with `scripts/liquid-glass/reference.html` and `reference.ps1`, five boxes at
display scales 1, 1.25 and 1.5. The tests read the committed crops in
`tests/PowerLedger.App.Tests/Aero/LiquidGlass/Reference`.

- **The backdrop is the box.** A `backdrop-filter` reads only what is behind the element's own border box. The blur and
  the displacement both read it with Skia's mirror tiling (the edge texel repeated), so nothing from outside a piece ever
  shows in it. A piece needs no room around it.
- **The turbulence is the SVG reference algorithm** (Park and Miller random numbers, seed 0 read as 1, the same
  lattice and gradients, noise2, the `turbulence` sum of absolute noise over octaves). It differs in three ways:
  - Skia evaluates it at user point `((i + 1) / s, (j + 1) / s)` for device pixel `(i, j)` at display scale `s`. The
    one pixel shift is Skia's "WebKit's 1 based coordinates", and the GPU path floors the pixel centre first.
  - Chromium's GPU path reads each gradient back from a 16 bit packing in an 8 bit texture as `2v / 65280 - 1`. Every
    gradient comes out about 0.4 % longer and nudged by 1/256. With the reference gradients 71 % of displaced pixels
    land on Chromium's; with the packed ones, 99.6 to 99.7 %.
  - The turbulence lands in an 8 bit premultiplied texture. feDisplacementMap unpremultiplies it in float. Both are in
    linearRGB, so nothing is converted between them.
- **The move** is `scale × s × (channel − 0.5)` device pixels. The sample is the texel under the pixel centre plus the
  move, mirrored into the box.
- **The source graphic goes through 8 bit linearRGB and back**, because feDisplacementMap works in linearRGB. Dark
  colours come out in coarser steps.
- **brightness(1.1)** clamps and lands in 8 bits. **blur(2px)** is Skia's separable Gaussian of deviation 2 s, radius
  ceil(3 σ), each pass in 8 bits, read as the centre texel plus bilinear pairs. Rounding is the hardware's, not
  `floor(x + 0.5)`: brightness makes every tenth input land exactly half way.
- **CSS order.** A filter list runs left to right, each function on the one before's output. So the order is brightness,
  then blur, then the displacement reading the blurred picture.
- **A backdrop-filter element is a backdrop root.** A backdrop-filter inside it reads the outer element's own surface:
  its background and the content painted before the inner element, over transparent; not the page, not the outer
  backdrop. Edge draws the mockup's Energy pane with its fourteen glass bars byte for byte as the pane alone
  (`scripts/liquid-glass/dashboard.html`), and the ring's glass hole bends the ring (`nested.html`). The inner result is
  laid over the outer surface, which WPF's drawing order gives exactly: outer glass, then the content, then the inner
  glass over it.
- **Half a device pixel snaps up.** The pane at CSS x 274 is device x 343 at 1.25 (not .NET's round to even, 342).

## How it works

### Three paths, chosen per window

| Path | Where | How the glass is drawn and shown |
|---|---|---|
| **Composed** (main) | any window, when Direct3D 11 at feature level 11_0, DirectComposition and hardware WPF are there | D3D11 draws each piece from the duplicated desktop into a layer. A companion window shows the layer through DirectComposition, laid exactly beneath the owner, so WPF never redraws for a glass frame |
| **Imaged** | Windows refused the companion window or a composition device | the same D3D11 drawing into one shared picture, shown through one D3DImage (bridged by Direct3D 9Ex), each piece showing its own rectangle |
| **Effects** | remote sessions, WPF in software, no feature level 11_0, three lost devices in a minute | the window's rectangle read back to the CPU into a WriteableBitmap; four nested ps_2_0 WPF effects per piece |
| **Wallpaper** | screenshots allowed, capture unavailable (older Windows, policy, a rotated monitor), an HDR desktop on the effects path | the wallpaper where Windows draws it, through the effects path |
| **Inside** | a piece inside another piece, on every path | the outer element's content beneath it (`InsideGlassSource`), through the recipe on the CPU (`GlassRecipeCpu`), shown as one bitmap; no capture |

### Capture (`MonitorCapture`)

- DXGI desktop duplication runs per monitor, shared by every window of ours on it.
- Our windows, and their companions, are left out of capture with `WDA_EXCLUDEFROMCAPTURE`.
- One background thread waits for a frame (AcquireNextFrame blocks until something changes) and copies the frame's
  dirty and moved rectangles into a desktop copy on the GPU. The governor allows at most 30 frames a second.
- **Our own repainting costs one compare.** On the GPU path, the part of the dirty rectangles under a window's pieces is
  compared on the GPU (a compute shader) with the copy before. Pieces are drawn only if a pixel really differs; only the
  compare's one word is read back. On the effects path, the part is read back and compared on the CPU, a few rows
  first.
- **A change past our windows is drawn without a compare.** When a changed rectangle under the glass reaches past every
  window of ours (a video playing behind), it is something else's: drawn with no compare and no wait for its answer.
  Only a change wholly inside one of our windows is compared, as it may be that window repainting.
- **Quiet frames slow the pace.** After a frame that draws nothing, the next look waits a tenth of a second; the first
  frame that draws brings back the governor's pace.
- With every window hidden or minimised, the duplication is let go and the thread sleeps: no frames.
- When a window is left out of capture or starts its glass, every frame copies the whole desktop for half a second.
  Windows can hand over a frame drawn before the exclusion took hold, and no dirty rectangle would ever take that out of
  the copy.
- **HDR.** `IDXGIOutput5::DuplicateOutput1` takes the desktop as scRGB half floats. The shaders divide by the monitor's
  SDR white level (DisplayConfig `GET_SDR_WHITE_LEVEL`), clamp, encode as sRGB and round to 8 bits, which is what the
  browser shows in an SDR window there.
- **A removed device** is made again with everything on it. After three losses in a minute the window falls back to the
  effects path.
- **Mode changes** (access lost) reopen the duplication, and a display change restarts the window's source.

### Drawing (`GpuGlassRenderer`, `Shaders/GpuGlass.hlsl`)

- **Bright** (ps_4_0): brightness on the box and a margin of `2 × pairs + 1` texels, the margin reading the box
  mirrored.
- **Across**: the Gaussian across as Skia reads it, the centre plus bilinear pairs.
- **Down**: the Gaussian down at the texel the source map names, rounded; then through 8 bit linearRGB and back. On the
  composed path it also writes the piece's shape into alpha: its rounded corners, its ancestors' clips (a scrolled
  page's viewport) and their opacity (a fade).
- **Compare** (cs_5_0): one word per window, set when any texel differs.
- **The source map** (`DisplacementField.SourceMap` / `Source`) is computed on the CPU once per piece size, display
  scale and displacement scale. The turbulence is shared: after the first piece at a scale, a new size is only a mirror
  and a pack. A piece keeps the maps of its last six sizes (the watts pill changes width with its reading and comes
  back), and a map in the making for a piece resized again is made once more, for where it ends.

### Showing

**Composed** (`GpuGlassWindow`, `GlassCompanion`):

- The companion is a borderless, unactivated `WS_EX_NOREDIRECTIONBITMAP` popup over the owner's client area. A
  DirectComposition target on it shows a premultiplied swap chain the size of that area.
- It is kept directly beneath the owner in the z-order. A topmost owner makes it topmost too.
- Its window region is the union of its pieces' shapes, so it never takes a click meant for what lies between them.
- A DirectComposition target on the owner itself would be drawn over WPF's content whatever its topmost flag says,
  because that flag places it only against child windows. That was measured: the text vanished.
- Pieces are drawn in WPF's drawing order (the visual tree's), so a card or a dialog is drawn over the glass beneath it.
- Springs, slides and fades move pieces without a layout pass. AeroMotion tells the engine
  (`LiquidGlassSources.Animate`), and while anything animates each piece takes a quick look every frame: its transform
  to the root and its ancestors' opacity. It is placed again only when that look changed.

**Imaged**: the D3DImage is kept locked while the capture thread may draw.

- After a frame is drawn, the UI thread marks the pieces' rectangles dirty and unlocks.
- WPF sends the copy as it commits its next frame. From the frame after that, the UI thread tries the lock again
  without waiting.
- `TryLock` counts a lock even when it fails (`D3DImage.LockImpl`), so a failed try is undone at once. A lock taken
  before the commit would cancel the copy.
- A piece that shows nothing lets go of the D3DImage. WPF marks every user of a D3DImage dirty when it changes, hidden
  or not.

**Effects** (`LiquidGlassEffects`, `Shaders/*.fx`, ps_2_0 so WPF's software renderer runs them too):

- Brightness with the margin mirrored, then the blur across, then down, then the displacement through a relative-move
  map and the linearRGB trip.
- The picture is the window's one source image, laid where it lies on the screen and clipped. A brush viewbox would
  make WPF cut the picture on the CPU for every piece and frame.
- The finished glass is cached where WPF draws in hardware. In software, a cached chain of effects is drawn out of
  place, so there it isn't cached.

**Inside** (`InsideGlassSource`): the inner piece's box of the outer element's content, drawn in WPF's painting order up
to the inner piece (each visual's own drawing with its offset, transform, clip and opacity; backdrops and
`InBackdrop=False` parts left out), rasterised by a `RenderTargetBitmap` at device pixels and run through the recipe on
the CPU (`GlassRecipeCpu`: the effects' passes at their precision), so WPF draws one bitmap and no effects. It is looked
at once after a burst of layout passes and each frame while AeroMotion animates: only what reaches the box goes into a
hash (where it lands, the opacity and clips over it, its drawing), and only a new hash or size draws again. Transforms
are walked from each visual's offset and transform: WPF's TransformToAncestor works out every ancestor's drawing bounds
under an Effect. A visual's Effect (a text shadow) is not drawn: a
`DrawingGroup` has none. A colour changed without a layout pass shows at the next one.

Since the merge with the mockup's layout (0.10.9, 22 nested pieces on the Dashboard), three things keep it off the
frame's critical path:
- **The piece's own frame.** Everything is drawn and hashed in the piece's coordinates (the chain to the outer element,
  inverted), and an opacity or mask on the outer element or the piece's own ancestors is left to the piece, as an
  opacity group's is in the browser. A pane's content gliding and fading in with its bubbles draws nothing again, and the
  fade is taken once, not twice.
- **A share of each frame.** While something animates, the nested pieces share 5 ms of a frame (`InsideGlassBudget`):
  the first is always looked at, the rest wait for a later frame, and a piece still behind when the animation ends is
  looked at once more before it lets go of the frame. Before: 150 to 580 ms frames as the bars grew; after: under 50 ms
  in a Debug build.
- **Tables.** Brightness and the linearRGB trip are functions of a channel's value and alpha alone, so each is one look up
  in a 64 KB table; the bytes are the sums they table. The recipe runs 2.2 to 2.8 times faster (a 50 by 375 bar: 32.5 to
  11.7 ms, Debug).

### Shaders

`scripts/liquid-glass/compile-shaders.ps1` compiles every shader with Windows' own `d3dcompiler_47.dll`, so no SDK or
fxc is needed:

- `*.fx` to `.ps` for ps_2_0;
- `GpuGlass.hlsl` to `GpuGlass.<entry>.cso` for vs_4_0, ps_4_0 and cs_5_0.

The binaries are committed and embedded. To change a shader, edit it, run the script and commit what it writes.

### The governor

`LiquidGlassGovernor` is the safety net on both paths. Once a second while the glass takes frames, it reads the
process's CPU:
- over the budget (8 %), the next frame waits half as long again, down to one a second;
- under seven tenths of the budget, it comes a quarter sooner, up to 30 a second.
A window at rest takes no frames and is never slowed.

## Measurements (2026-10-01, commit 4bb857c)

### Parity with headless Edge

| What | Path | Channels equal | Within 8 |
|---|---|---|---|
| Displacement sources, cases a to e | turbulence port | 99.6 to 99.7 % of pixels on Chromium's own | the rest within 0.02 of a rounding boundary |
| Case e at 1.25 | GPU, composed | 99.77 % | 99.92 % |
| Energy pane over the mockup's background, 1.25 | GPU, composed | 99.80 % | 99.99 % |
| Case e at 1.25, on screen | WPF effects, hardware | 99.79 % | 99.92 % |
| Cases a, d | WPF effects, software (bitmap) | 95.86 %, 94.55 % | 99.9 % |
| Energy pane, 1 and 1.25 | WPF effects, software | 94.97 %, 95.71 % | 99.99 % |
| Cases a, d, e | CPU recipe | 96.61 %, 95.30 %, 96.52 % | 99.9 % |
| Glass inside glass (ring hole, bubbles, capsule), 1 and 1.25 | inside, CPU recipe | 93.99 %, 94.97 % (the ring hole's middle 99.61 %, 99.79 %) | 99.9 % |

### CPU (Release, 30 s each, a reading every second; `LiquidGlassCpu`, manual)

Three rounds without the governor, then two with it. Each run waits for the runtime's tiered compilation to settle;
what it still compiled is given apart. The layout agent's on-screen tests ran throughout, so the "at rest" scenes still
saw 320 to 570 desktop changes in 30 s.

| Scene | Glass on (3 rounds) | Less compiling | Governed (2 rounds) | Glass none | Target |
|---|---|---|---|---|---|
| Main window over a video | 4.48, 5.68, 5.99 % (about 21 frames a second, the video's own) | 4.33, 5.33, 5.70 % | 4.90, 4.53 % | 1.8 to 2.4 % | 8 %: met |
| Main window at rest | 3.33, 5.42, 3.23 % | 3.12, 3.55, 3.12 % | 2.71, 3.33 % | 1.7 to 2.0 % | 2 %: not met; the look alone is 1.7 to 2.0 % |
| Overlay over a video, main hidden | 3.49, 3.64, 3.49 % | 3.20, 2.75, 2.61 % | 2.81, 3.44 % | 0.4 to 0.8 % | 3 %: met once steady, at the line raw |
| Overlay at rest | 1.93, 2.50, 1.87 % | | 1.61, 2.19 % | 0.6 to 0.9 % | |
| Main window hidden | 0.78, 1.30, 0.68 %, no frames | | 0.99, 1.15 %, no frames | | 0 frames: met |

Per frame on the capture thread (thread cycle counters, overlay over a video): Present 0.72 M cycles, the layer copied
to the back buffer 0.33 M, acquiring and releasing the frame 0.57 M, drawing the piece 0.07 M.

### Which path, when

| Case | Path |
|---|---|
| A local session, WPF in hardware (tier 2), Direct3D 11 at 11_0, Windows 10 2004 or later | Composed |
| Windows refuses the companion window or a composition device | Imaged |
| A remote session, WPF in software or below tier 2, no 11_0, three lost devices in a minute | Effects, live capture read back |
| Screenshots allowed; capture unavailable (older Windows, policy, a rotated monitor, duplication failing); HDR on the effects path | Wallpaper, through the effects |
| A piece inside another, on any of these | Inside: the outer content beneath it, the recipe on the CPU, one bitmap |
| `IsLive` false (reduce transparency, glass off) | nothing |
| Every glass window hidden or minimised | the duplication let go, no frames |
