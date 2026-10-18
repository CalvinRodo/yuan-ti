# Dark Sun GFF Viewer — Data & Code Reference

This file is a complete reference for an AI agent working on this codebase.
It covers the GFF binary format, every supported resource type, the Python
extraction API, the Flask server API, and the web frontend architecture.
Read this first; inspect source only when something unexpected comes up.

---

## 1. Project Layout

```
src/
  gff.py          # Binary parser — GffFile, ResourceDescriptor, index tables
  image.py        # Image decoding — ImageFrame, Palette, ImageReading
  image_tool.py   # frame_to_pil_image(), CLI wrapper
  bit_chomper.py  # BitChomper — reads 1-16 bits at a time from a byte array
  server.py       # Flask server — all /api/* endpoints
  web/
    app.js          # Core state, mode navigation, PAL selector, routing
    view-images.js  # Image grid, image list, detail pane, resource table
    view-map.js     # Region tile-map canvas viewer
    view-spells.js  # Spell card list
    view-text.js    # Text/MERR/SPIN/NAME list + reader
    index.html      # App shell
    style.css       # Full dark-theme stylesheet
dark_sun/
  RESOURCE.GFF    # Palettes, spells (SPIN), text (TEXT/MERR), icons, names
  RGN*.GFF        # Region files — tiles (TILE), maps (RMAP), region data
  CINE.GFF        # Cinematic resources
  GPLDATA.GFF     # Compiled GPL scripts
  *.GFF           # Other game data files
```

---

## 2. GFF Binary Format

File is little-endian throughout. Translated from John Glassmyer's Java port.

### File Header

| Offset | Type    | Meaning                        |
|--------|---------|--------------------------------|
| 0      | bytes   | File identifier / padding      |
| 12     | int32   | Offset to the GFFI index block |

### Index Block (at offset from header[12])

```
[+0]  int32  (skip)
[+4]  int32  (skip)
[+8]  int16  numberOfTags

For each tag:
  4 bytes  ASCII tag name (e.g. b'BMP ')
  int32    n_chunks  (SIGNED — negative means secondary table)

  If n_chunks > 0:  → PrimaryGffiTable starts at this int32 position
  If n_chunks == 0: → Secondary table described by three more ints:
      int32  (skip)
      int32  secondaryTableIndex  (index into GFFI primary table)
      int32  segmentCount
      For each segment: int32 startResourceNumber, int32 segmentCount
```

### Index Table Types

**PrimaryGffiTable** — 12 bytes per entry:
```
int32 resourceNumber | int32 offset | int32 size
```

**SecondaryGffiTable** — 8 bytes per entry:
```
int32 offset | int32 size
```
Resource numbers are derived from numbering segments in the index block.
A `GFFI` tag always exists as the primary index for secondary tables.

---

## 3. Python Extraction API

### `gff.py` — Core classes

```python
from gff import GffFile, NoSuchResourceError, GFF_CHUNK_TYPES

# Load a GFF file (returns GffFile)
with open('RESOURCE.GFF', 'rb') as f:
    gff = GffFile.create(f.read())

# List all resources → List[ResourceDescriptor]
for desc in gff.describe_resources():
    print(desc.tag, desc.number, desc.offset, desc.size)

# Extract raw bytes for one resource
raw: bytes = gff.get_resource_data('BMP ', 42)

# Check existence
exists: bool = gff.has_resource('PAL ', 0)

# ResourceDescriptor fields:
#   .tag     str  — 4-char ASCII tag (may have trailing space, e.g. 'BMP ')
#   .number  int  — resource number
#   .offset  int  — byte offset in file
#   .size    int  — byte size
```

**`GffiTable.describe_resources()`** returns descriptors sorted by offset.

**`GffFileList`** — wraps multiple GffFile instances and searches them
in order. Useful when a resource might be in the current GFF *or*
`RESOURCE.GFF`.

**`GFF_CHUNK_TYPES`** — `dict[str, str]` mapping tag → human description, loaded from `src/data/gff_chunk_types.toml`.
All known tags are listed there. Tags not in this dict show as `'Unknown'`.

### `server.py` — `load_gff(name)`

The server caches up to 10 GFF objects in `_gff_cache`. Always use
`load_gff(name)` rather than opening files directly; name is uppercased
automatically.

---

## 4. Resource Types

### 4a. Image Resources: `BMP `, `TILE`, `ICON`

Constant: `IMAGE_TAGS = {'BMP ', 'TILE', 'ICON'}`

All three use the same binary image format decoded by `ImageReading`.

#### Binary image format

```
[0]  int32  file size (ignored)
[4]  int16  frameCount
[6]  int32 × frameCount  — frame offsets from start of resource bytes

Each frame, starting at its offset:
  [+0]  int16  width
  [+2]  int16  height
  [+4]  byte   0xFF → planar (PLAN / PLNR)  OR  row number → row-based
```

Three sub-formats:

| Marker at [+4] | Sub-format | Decoder |
|----------------|------------|---------|
| Not 0xFF       | **Row-based RLE** | `_read_row_based_frame()` |
| 0xFF + `PLAN`  | **PLAN planar** | `_read_planar_frame(..., _plan_symbol_source)` |
| 0xFF + `PLNR`  | **PLNR planar** | `_read_planar_frame(..., _plnr_symbol_source)` |

Row-based RLE per-run header (4 bytes):
```
byte startX       (add 256 if COLUMN_256_FLAG set in flags)
byte flags        bit7=LAST_RUN_FLAG, bit0=COLUMN_256_FLAG
byte uncompLen
byte compLen
then compLen compressed bytes
```
Pixel RLE inside a run: even code byte = literal run, odd = repeat run.

Planar layout after height/width:
```
[+4]  0xFF
[+5]  4-byte tag ('PLAN' or 'PLNR')
[+9]  bitsPerSymbol (0 = empty frame)
[+10] dictionary (2^bitsPerSymbol pixel values)
rest  bit-packed symbol stream, big-endian
```

#### Extracting images in Python

```python
from image import ImageReading, Palette
from image_tool import frame_to_pil_image

raw = gff.get_resource_data('BMP ', number)
frames = ImageReading.extract_frames(raw)   # → List[ImageFrame]

pal_raw = gff.get_resource_data('PAL ', 0)
palette = Palette.from_pal_data(pal_raw)

for frame in frames:
    pil_img = frame_to_pil_image(frame, palette)  # → PIL.Image (RGBA)
```

`ImageFrame` properties:
- `.width`, `.height` — int
- `.pixels` — `bytearray` of palette indices, row-major (0 = transparent)
- `.alpha_mask` — `bytearray` bitmask, 1 = opaque

#### Server endpoint

```
GET /api/gff/<name>/image/<tag_url>/<number>
    ?frame=<int>        # specific frame; -1 = full sprite sheet (default)
    ?pal_gff=<name>     # GFF holding the palette
    ?pal_num=<int>      # PAL resource number
    ?pal_tag=<PAL|CPAL> # palette tag (default PAL)
→ PNG image
```

Tag URL encoding: trailing spaces become underscores.
`'BMP '` → `'BMP_'`, `'TILE'` → `'TILE'`, `'ICON'` → `'ICON'`.

Helper: `tag_to_url_seg(tag)` / `url_seg_to_tag(seg)` in `server.py`.

```
GET /api/gff/<name>/image/<tag_url>/<number>/info
→ JSON { frames: [{index, width, height}] }
```

---

### 4b. Palette Resources: `PAL `, `CPAL`

`PAL ` — VGA 256-colour palette. Each colour is 3 bytes (R, G, B) in the
range 0–63; multiply by 4 to get 0–252 values.

`CPAL` — same format (colour palette), different tag used by some GFFs.

```python
from image import Palette

pal_raw = gff.get_resource_data('PAL ', 0)
palette = Palette.from_pal_data(pal_raw)   # 256 Color objects
color   = palette.get_color(index)         # .red .green .blue (0-252)
```

Palette priority (implemented in `server.py:find_palette()`):
1. Explicit `pal_gff` + `pal_num` + `pal_tag` query params
2. First `PAL ` in the same GFF
3. First `PAL ` in `RESOURCE.GFF`

```
GET /api/gff/<name>/pals
→ [{gff, number}]  (per-GFF pals, RESOURCE.GFF padded in)

GET /api/pals/all
→ [{gff, tag, number}]  (every PAL/CPAL across all GFFs)
```

---

### 4c. Tile Map: `RMAP`

Flat binary: one byte per tile cell, row-major order.
Size is usually a perfect square (e.g. 64×64 = 4096 bytes).
Server auto-detects square size; non-square falls back to width=size, height=1.

```python
raw = gff.get_resource_data('RMAP', number)   # bytes
# Each byte = tile index into the TILE resource set
```

```
GET /api/gff/<name>/rmap/<number>
→ { width: int, height: int, tiles: [int] }
```

Tile images come from `TILE` resources numbered 0–255 in the same GFF.

---

### 4d. Spell Information: `SPIN`

Variable-length latin-1 text, one resource per spell.
Format: `"SpellName: Description text"` (split on first `: `).
Null-terminated in some GFFs.

```python
raw = gff.get_resource_data('SPIN', number)
text = raw.decode('latin-1').strip()
name, _, desc = text.partition(': ')
```

Icon lookup: corresponding ICON resource is at number `SPIN_number + 21000`
(defined as `SPIN_ICON_OFFSET = 21000` in `server.py`).

```
GET /api/gff/<name>/spells
→ [{number, name, description, icon_number}]
  icon_number is null if the ICON doesn't exist in this GFF
```

---

### 4e. Text Resources: `TEXT`, `MERR`, `NAME`, `SPIN`

All decoded the same way — latin-1, null-terminated, `\r\n` normalized to `\n`:

```python
raw  = gff.get_resource_data('TEXT', number)   # also MERR, NAME, SPIN
text = raw.split(b'\x00')[0].decode('latin-1').replace('\r\n', '\n').replace('\r', '\n')
```

| Tag    | Meaning                                 | Typical GFF   |
|--------|-----------------------------------------|---------------|
| `TEXT` | Generic text strings (names, labels…)   | RESOURCE.GFF  |
| `MERR` | Error message strings                   | RESOURCE.GFF, CINE.GFF |
| `SPIN` | Spell info (see §4d for extra parsing)  | RESOURCE.GFF  |
| `NAME` | Named entity labels                     | Various       |

Constant: `TEXT_TAGS = {'TEXT', 'MERR', 'SPIN', 'NAME'}` in `server.py`.

```
GET /api/gff/<name>/texts
→ [{tag, number, size, content}]
  tag is stripped of trailing spaces (e.g. 'TEXT', not 'TEXT')
```

---

### 4f. Other Known Tags (not yet viewer-supported)

| Tag    | Description                    | Notes |
|--------|--------------------------------|-------|
| `PAL ` | Palette (see §4b)              | |
| `GMAP` | Region map flags               | byte per cell like RMAP |
| `GPL ` | Compiled GPL script            | decoded by `gpl_parser.py` (see dsun-exe-reference.md) |
| `GPLX` | GPL tables                     | `[0x3DE:0x441]` = stat field types used by GPL opcode 33 |
| `MAS ` | Compiled MAS files             | same bytecode as GPL; decoded by `gpl_parser.py --tag=MAS` |
| `FONT` | Font resource                  | |
| `ETAB` | Object entry table             | |
| `POBJ` | PolyMesh object database       | 3DX format |
| `SCMD` | Animation script               | |
| `SJMP` | OENGINE animation              | |
| `MSEQ` | XMIDI music sequences          | |
| `MGTL` | Global timbre library          | |
| `ACF ` | Cinematic binary               | CINE.GFF |
| `BMA ` | Cinematic binary file          | |
| `OMAP` | Opacity map                    | |
| `TMAP`/`TXRF` | Texture map               | |
| `CMAP` | Color map table                | |
| `BUTN`/`DBOX`/`EBOX`/`WIND`/`SBAR`/`MENU` | UI resources | |

---

## 5. Server API Reference

Base URL: `http://localhost:5000`

### GFF Lists

```
GET /api/gffs
GET /api/gffs?cap=maps       # only RGN*.GFF files
GET /api/gffs?cap=images     # GFFs with at least one BMP/TILE/ICON
GET /api/gffs?cap=spells     # only RESOURCE.GFF
GET /api/gffs?cap=texts      # GFFs with at least one TEXT/MERR/SPIN/NAME
→ ["RESOURCE.GFF", "RGN02.GFF", ...]
```

### Resources for a GFF

```
GET /api/gff/<name>/resources
→ [{tag, tag_url, number, size, offset, description, is_image, is_rmap}]
  is_image: bool — tag in IMAGE_TAGS
  is_rmap:  bool — tag == 'RMAP'
  tag_url:  trailing-space → underscore encoded tag for URL paths
```

### Images

```
GET /api/gff/<name>/image/<tag_url>/<number>?frame=<n>&pal_gff=<g>&pal_num=<n>&pal_tag=<t>
→ PNG
GET /api/gff/<name>/image/<tag_url>/<number>/info
→ {frames: [{index, width, height}]}
```

### Palettes

```
GET /api/gff/<name>/pals          → [{gff, number}]
GET /api/pals/all                 → [{gff, tag, number}]
```

### Maps / Text / Spells / Raw

```
GET /api/gff/<name>/rmap/<number> → {width, height, tiles: [int]}
GET /api/gff/<name>/texts         → [{tag, number, size, content}]
GET /api/gff/<name>/spells        → [{number, name, description, icon_number}]
GET /api/gff/<name>/raw/<tag_url>/<number> → application/octet-stream
```

---

## 6. Frontend Architecture

### `app.js` — State Machine

```javascript
state = {
  mode: 'spells'|'map'|'resources'|'images'|'text',
  gffByMode:    { spells:'RESOURCE.GFF', map:null, resources:null, images:null, text:null },
  gffListByMode:{ spells:[], map:[], resources:[], images:[], text:[] },
  resources:   [],   // current GFF resource list from /resources
  pals:        [],   // per-GFF PALs from /pals
  allPals:     [],   // cross-GFF PALs from /pals/all
  selectedPal: null, // {gff, tag, number} or null
  imageView:   'card'|'list',
  detail: { resource:null, frames:[], frameIndex:0 },
  get currentGff() { return this.gffByMode[this.mode]; }
}
```

Key functions:
- `activateMode(mode)` — switch mode, fetch GFF list, load previously-selected GFF
- `loadGffForMode(gff, mode)` — fetch `/resources` + `/pals`, update mapState, call `renderContent()`
- `fetchAllPals()` — fetches `/api/pals/all` once on boot into `state.allPals`
- `renderContent()` — dispatches to the correct view function based on `state.mode`
- `buildPalSelectorAll(allPals)` — builds `<select>` with `'a'+index` values from `allPals`
- `palParams()` — returns `&pal_gff=...&pal_num=...&pal_tag=...` string for image URLs
- `imageUrl(gff, tagUrl, number, frame)` — builds full image API URL with palette params
- `hideAllViews()` — hides all content divs
- `setImageView('card'|'list')` — toggles image view state and re-renders

DOM mode routing:
| Mode        | View rendered by         | Content element hidden/shown   |
|-------------|--------------------------|--------------------------------|
| `spells`    | `renderSpellGrid()`      | `#spell-grid`                  |
| `map`       | `showMapView()`          | `#rmap-view`                   |
| `resources` | `renderResourceTable()`  | `#resource-table-wrap`         |
| `images`    | `renderImageGrid()` / `renderImageList()` | `#image-grid` / `#image-list-wrap` |
| `text`      | `renderTextView()`       | `#text-view`                   |

### `dom` object keys (app.js)

Mode nav: `modeBtns, modeGffGroup, modeGffSelect, imgViewGroup, imgCardBtn, imgListBtn, palGroup, palSelect`

Content: `content, emptyState, emptyMsg, imageGrid, imageListWrap, imageListTbody, spellGrid, textView, textSearch, textList, textSearchCount, textReaderEmpty, textReaderContent, textReaderBadge, textReaderNum, textReaderBody, tableWrap, resFilterBar, tableTbody`

Map: `mapView, mapCanvas, mapCanvasWrap, mapWidthInput, mapSizeLabel, mapZoomLabel, mapZoomIn, mapZoomOut, mapResetPan, rmapSelect, mapSpinner`

Filters: `resFilterTag, resFilterDesc, resFilterCount, tagDatalist, descDatalist`

Detail pane: `detailPane, detailResizer, detailToggle, detailInner, detailTag, detailNum, detailSize, detailDesc, detailClose, detailImageView, detailPrev, detailNext, detailFrameInfo, detailImgWrap, detailImg, detailImgSpinner, detailImgDims, detailHexView, hexInfo, hexTruncated, hexPre`

### Palette value encoding

When `buildPalSelectorAll` is used (always), `<option>` values are `'a' + index` (into `state.allPals`).
`onPalChange()` checks `val.startsWith('a')` to pick from `state.allPals` vs `state.pals`.

### `mapState` (view-map.js)

```javascript
mapState = {
  rmapList:   [],    // RMAP resource descriptors for current GFF
  rmapNumber: null,  // currently selected RMAP number
  tiles:      null,  // {width, height, tiles:[]} from /rmap/<n>
  tileCache:  Map,   // tileNum → HTMLImageElement
  pan: {x,y}, zoom: 16, dirty: true
}
```

---

## 7. Adding New Server Endpoints

Pattern for a text-like resource (decode latin-1):
```python
@app.route('/api/gff/<name>/mytag')
def api_mytag(name: str):
    gff = load_gff(name)
    result = []
    for d in gff.describe_resources():
        if d.tag.strip() != 'MYTAG':
            continue
        raw  = gff.get_resource_data(d.tag, d.number)
        text = raw.split(b'\x00')[0].decode('latin-1').replace('\r\n', '\n')
        result.append({'number': d.number, 'size': d.size, 'content': text})
    return jsonify(result)
```

Pattern for adding a new cap filter to `/api/gffs`:
```python
elif cap == 'mynewcap':
    def has_thing(name):
        try:
            return any(d.tag.strip() == 'MYTAG'
                       for d in load_gff(name).describe_resources())
        except Exception:
            return False
    files = [f for f in files if has_thing(f)]
```

---

## 8. Adding a New Mode (full checklist)

1. **server.py** — add cap string to `/api/gffs` if list should be filtered
2. **index.html** — add `<button class="mode-btn" data-mode="mymode">` in `#mode-nav` + content `<div id="my-view" class="hidden">` in `#content`
3. **app.js**:
   - Add `mymode: null` to `gffByMode` and `gffListByMode`  
   - Add `ensureGffList('mymode')` to `init()`
   - Add `mymode: 'Select a ...'` to the empty message map in `activateMode()`
   - Add visibility toggle in `activateMode()` if toolbar groups need adjusting
   - Add `dom.myView.classList.add('hidden')` to `hideAllViews()`
   - Add `case 'mymode': renderMyView(); break;` to the switch in `renderContent()`
   - Add DOM ref in `dom` object
4. **view-mymode.js** — create, export `renderMyView()`; load in `index.html`
5. **style.css** — add styles for the new content div
