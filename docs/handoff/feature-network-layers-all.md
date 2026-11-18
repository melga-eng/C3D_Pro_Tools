# feature/network-layers-all

- Network Layers stage 2: pressure networks, network groups (ICT/other/clash), rules keyed by type + service + existing/proposed, learning only from KSIA-named layers, KSIA template suggestions (LayerRules.Suggest, unticked, never applied by the command line), missing layers copied from the template, profile labels resolved through ModelPartId.
- Checked: 797 tests; network-layers.scr (ICT: 1658 moved, 27 layers deleted, re-check leaves 36 suggested) and network-layers-all.scr (81 networks, 42 suggestions) on TEST DRAWING copies.
- Review (Codex): 3 fixed (service-scoped keys, profile labels, failed template copy skips the move instead of inventing the layer).
- Not done: the window not drawing-tested; stage 3 (text, dimension, title block, xref, purge checks).
