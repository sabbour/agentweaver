# One batched inspection and correction round

Opened the actual `candidate-initial.png` and `comparison-initial.png`.
Both comparison panels are 2140x2018; no aspect stretching or panel-specific resizing.
The initial exported SVG was also inspected to diagnose browser text positioning,
not to substitute for PNG inspection.

## Retained after inspection

- Two columns, original card widths in the raster, original row centers and margins.
- Requested 400x116 cards; 400x148 for the explicitly collapsed Merge/PR phase.
- Actual Fluent Regular SVG glyphs at38 units, semantic5-unit accents, radius16,
  warm palette, no heading/badges/debug text.
- The requested28/600 title is observably larger than the legacy raster's type.
  This discrepancy was identified before authoring; no claim of pixel-identical
  type is made. The requested tokens are preserved rather than disguising a
  smaller effective font under a CSS scaling transform.
- Separated return paths; labels anchored on their own routes; actual bridges at
  unrelated crossings and dots only at shared semantic branches/continuations.
- Arrowhead optical measurement is26 raster pixels in both original and candidate,
  so no arbitrary marker-size adjustment is made.

## Batched corrections

1. The draw.io HTML text wrapper adds approximately4.5 logical units of vertical
   displacement. Move the title/subtitle group upward4.5 units to recover the
   original icon/text optical baseline. Preserve28/600/1.15,20/1.2 and all padding.
   Original Agent-work ink box was(477,254)-(685,292); initial candidate was
   (468,258)-(715,303). This is a baseline correction, not hidden font rescaling.
2. Set the page backing white so the rounded warm canvas has visible corners,
   matching the React image. Keep the warm canvas itself unchanged.

Export exactly one corrected candidate and then perform the confirmation round.
Do not resume the shared migration or write public assets.

## Tooling corrections, not authoring iterations

The installed Fluent icon package does not export package.json and contains no
standalone LICENSE file. Provenance is therefore read from the installed package
metadata and recorded honestly; no license text was fabricated. These lookup failures
occurred before the first candidate export and did not create extra visual candidates.
