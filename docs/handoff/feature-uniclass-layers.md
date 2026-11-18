# feature/uniclass-layers

- Network Layers (NetworkLayers window, C3DPROLAYERS Check/Fix/Data): ICT parts on their type's layer (type from the description), labels from per-network labelling layers to the base; labels on viewport-frozen layers stay; rules learned and saved in the drawing (Xrecord C3DPRO_LAYER_RULES).
- Analysis behind it: KSIA CAD standard (Role-Uniclass-Presentation-Description), KSIA template as the layer master, Uniclass Oct 2026 tables (Codex), TEST DRAWING parts/labels data (C3DPROLAYERS Data).
- Checked: 784 tests; tests/drawing/network-layers.scr on TEST DRAWING copy: 320 moved, re-check leaves 36 parts with no rule.
- Review (Codex): 3 findings fixed (stale network list at Fix, tile label layers protected by viewport freeze, unticked items kept unticked).
- Not done: the window not drawing-tested; other networks; text/dim/title-block/xref checks.
