"""Review helper: tile the first view of each preview strip into one contact sheet.

  Blender -b --factory-startup --python art-source/blender/contact_sheet.py -- OUT.png Name1 Name2 ...
"""
import os
import sys
import numpy as np
import bpy

HERE = os.path.dirname(os.path.abspath(__file__))
argv = sys.argv[sys.argv.index("--") + 1:]
out, names = argv[0], argv[1:]
cols = 4 if len(names) > 6 else len(names)
tiles = []
for n in names:
    im = bpy.data.images.load(os.path.join(HERE, "previews", n + ".png"))
    w, h = im.size
    a = np.empty(w * h * 4, dtype=np.float32)
    im.pixels.foreach_get(a)
    a = a.reshape(h, w, 4)
    tiles.append(a[:, :h])          # first square view
    bpy.data.images.remove(im)
h = tiles[0].shape[0]
rows = (len(tiles) + cols - 1) // cols
canvas = np.zeros((rows * h, cols * h, 4), dtype=np.float32)
canvas[..., 3] = 1
for i, t in enumerate(tiles):
    r, c = divmod(i, cols)
    r = rows - 1 - r
    canvas[r * h:(r + 1) * h, c * h:(c + 1) * h] = t
img = bpy.data.images.new("sheet", cols * h, rows * h, alpha=True)
img.pixels.foreach_set(canvas.ravel())
img.filepath_raw = out
img.file_format = 'PNG'
img.save()
