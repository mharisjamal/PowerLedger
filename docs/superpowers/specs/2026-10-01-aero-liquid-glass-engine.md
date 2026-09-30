# Aero liquid glass engine (2026-10-01)

Part 1 of 2 of the owner's liquid glass for the Aero look. This part is the engine: the live backdrop, the filter
chain and the one element the look puts behind each glass piece. Part 2 (`plan-lg/look`) owns the look: styles, edges,
the drop shadow, the highlights and which pieces are glass.

## Source of truth

`polidario/Frontend-Projects`, `liquid-glass-vue/src/components/AppCard.vue` (no licence: reimplemented from its
parameters, no code copied). Its glass:

| Part | Value |
|---|---|
| backdrop-filter | `brightness(1.1) blur(2px) url(#displacementFilter)`, in that order |
| url filter | `feTurbulence type=turbulence baseFrequency=0.01 numOctaves=2` (seed 0, stitchTiles no-stitch) into `feDisplacementMap in=SourceGraphic in2=turbulence scale=200 xChannelSelector=R yChannelSelector=G` |
| color-interpolation-filters | default, linearRGB |
| filter | `drop-shadow(-8px -10px 46px #0000005f)` |
| radius | 28 px, no tint |
| ::before | `inset 6px 6px 0 -6px rgba(255,255,255,.7), inset 0 0 8px 1px rgba(255,255,255,.7)` |

`LiquidGlassRecipe` holds every one of these as a constant. CSS pixels are WPF units.

## API for the look

All in `PowerLedger.App.Aero` (namespace of `src/PowerLedger.App/Aero/LiquidGlass`), internal like the rest of Aero.

### `LiquidGlassBackdrop : FrameworkElement`

Put it as the bottom layer of a glass piece, filling the piece (same size and position as the piece's rounded rect).
It draws the filtered live backdrop, clipped to its own rounded rectangle, and nothing else: no tint, shadow, rim or
highlight. It is not hit-testable and not focusable.

| Property | Type | Default | Meaning |
|---|---|---|---|
| `CornerRadius` | `CornerRadius` | 28 | the clip's corners |
| `Brightness` | `double` | 1.1 | first filter |
| `BlurDeviation` | `double` | 2 | second filter, Gaussian standard deviation in units |
| `Scale` | `double` | 200 | third filter, displacement scale in units (0 turns displacement off) |
| `IsLive` | `bool` | true | false draws nothing and releases the capture (reduce transparency, a hidden piece) |
| `Kind` (read only) | `LiquidGlassSourceKind` | `None` | `Live`, `Wallpaper` (fallback) or `None` |

```xml
<Grid>
    <aero:LiquidGlassBackdrop CornerRadius="28" />
    <!-- the look's highlights, rim and content above -->
</Grid>
```

The drop shadow belongs outside the piece (the look draws it; it must not sit under the backdrop, which is opaque).

It works in any top-level window: the Aero window, the watts overlay (its own layered topmost window), popups and
menus. The first backdrop that loads in a window excludes that window from capture and starts its source; the last one
to unload stops it.

### `ILiquidGlassSource`, `LiquidGlassSources`

`ILiquidGlassSource` is the picture behind one top-level window: `Kind`, `Image` (an `ImageSource`), `ScreenBounds`
(physical pixels on the virtual screen) and `Changed`. `LiquidGlassSources.Override`, a
`Func<HwndSource, ILiquidGlassSource>`, hands every window a fake source: the render harness and tests set it so
nothing captures the real screen.

### `LiquidGlassRecipe`

The constants above, plus `Reach` (107 units): how far outside a piece the backdrop is read.

## How it works

(Filled in as each part lands: turbulence, shaders, capture, fallback, measurements.)
