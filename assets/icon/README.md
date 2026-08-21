# Knapper icon assets

Mark: a knapped obsidian point — five flake facets struck from the tip.

## Files
- `knapper-icon-dark.svg` — primary mark, for dark UI (violet #8F78EA family)
- `knapper-icon-light.svg` — same geometry, deeper violet (#5C47AD family) for light backgrounds
- `knapper-icon-small-*.svg` — 3-plane simplification; use at 20px and below
- `knapper-icon-mono.svg` — single silhouette, uses `currentColor`
- `knapper-icon-mono-cut.svg` — silhouette with two facets cut out, `currentColor`
- `knapper-tile.svg` — 512px rounded app tile with background
- `knapper-wordmark-{dark,light}.svg` — mark + "Knapper" lockup (text is a `<text>` element; convert to outlines before shipping if font portability matters)
- `png/` — rasters at 16–1024px; 16/20/24/32 use the simplified 3-plane mark, 48+ use the full 5-facet mark; `knapper-tile-*.png` for app/store icons (180 = apple-touch-icon)

## Usage
- Do not rotate the mark; the tip points up.
- Minimum clear space: 25% of the mark's height on all sides.
- Below 20px always use the small/simplified variant.
- On light backgrounds use the light variant, not the dark one at lower opacity.

## Palette
| Role | Dark UI | Light UI |
| --- | --- | --- |
| Body | #8F78EA | #5C47AD |
| Upper-left facet | #C9BAFF | #9781E8 |
| Upper-right facet | #7059CD | #4A3894 |
| Lower-left facet | #C9BAFF | #9781E8 |
| Lower-right facet | #5F49B8 | #3C2C7C |
