# feature/surface-from-solids

- C3DPROSURFSOLID: TIN surface from 3D solids (Brep exterior loops, outward by containment test) and SubDMesh/PolyFaceMesh (closed: outward by volume; open sheets: both sides); Top/Bottom/Both; 3D faces with edges kept, then erased; TIN edges not among the objects' edges deleted.
- Checked: 829 tests; tests/drawing/surface-from-solids.scr on Drawing122 copy (384 -> 384 triangles), solids only (top/bottom 50 mm apart).
- Review (Codex): 5 handled: separate pieces via edge deletion (not multiple outer boundaries), inward closed meshes flipped, curved/holed faces and plan overlaps reported.
- Not done: no window (prompts, as asked); curved solid faces follow their corners only; overlapping objects are not resolved to the highest/lowest face.
