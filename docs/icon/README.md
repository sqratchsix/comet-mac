# App icon

- `comet-icon-source.png` – the original 1024px artwork.
- `comet-mark.svg` – the comet traced from it, on a transparent canvas.
- `comet-icon.svg` – the app icon: the comet on a dark tile laid out on Apple's
  icon grid (an 824px rounded square, radius 185, centred on a 1024px canvas).

`build/COMET.icns` (the macOS app icon) and `src/Comet.Desktop/Assets/comet.ico`
(the window icon, and the Windows icon) are rendered from `comet-icon.svg`, every
size drawn from the vector rather than scaled down:

- `.icns`: 16, 32, 128, 256 and 512 points at 1x and 2x, assembled with `iconutil`.
- `.ico`: 16, 24, 32, 48, 64, 128 and 256 px, stored as PNG entries.

The comet colour is `#004ECB`, sampled from the source artwork.
