# Pass 2: correction only

Inspected pass-1 input PNG. Removed the database-only `size=5` override from
`assembly-icon` and `children-icon`, retaining their original native predefined-process
shape. This restores the two clipped vertical subprocess bars. No content, colors,
card geometry, boundaries, reading direction or connector endpoints changed.

Exported a distinct source/PNG with pinned draw.io Desktop 31.4.5 and the repository
PNG recipe. Opened the actual 1664 x 1172 output enlarged and the 794 x 559, 96-DPI
A5-size inspection sheet. Native bars are now visible without clipping; adjacent
titles, subtitles and content retain their positions. Checked all neighboring labels,
the two leftward output/review arrows and the outer return rail: clean.

Remaining orientation defects: 0. Overlap defects: 0. Arrow defects: 0.
The meaningful semantic growth count is unchanged because `size` is not a counted key.
