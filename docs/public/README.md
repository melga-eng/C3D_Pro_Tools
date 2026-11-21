# C3D Pro Tools 1.0

Three free tools for Civil 3D 2026, made by a civil engineer for daily utility work.

| Tool | What it does |
|---|---|
| **Navisworks Import** | Select pipes, duct banks or structures in Navisworks Manage. They come into Civil 3D as real pipe network parts at the right size and level. Parts that belong to a network already in your drawing join that network and connect to its structures. |
| **Surface from Solids** | Makes a TIN surface from the top, the bottom or both sides of 3D solids and meshes, for example a road made of solids with its daylight as a mesh. The objects' own triangles are kept, so gaps stay open. |
| **Rebuild Network** | Draws a network you cannot edit (a data-shortcut reference whose source is gone) again as an ordinary network, with the same structures, pipes, levels and connections. |

## Requirements

- **Civil 3D 2026, update 2026.2.2 or later** (Windows, 64-bit).
- **Navisworks Manage 2026**, for Navisworks Import only. Install it before C3D Pro Tools so its add-in is placed.
- Metric drawings.

## Install

1. Close Civil 3D and Navisworks.
2. Run **C3DProTools-Public.msi**. Windows may warn that the publisher is unknown: choose **More info → Run anyway**.
3. Start Civil 3D. A **C3D Pro Tools** tab appears on the ribbon.

To uninstall, use Windows **Settings → Apps → C3D Pro Tools**.

## Navisworks Import

1. Open your model in Navisworks Manage and select the items to bring in. Whole layers and groups are fine.
2. In Civil 3D: **C3D Pro Tools → Navisworks Import**, then **Get selection from Navisworks** (or F5).
3. Check the list:
   - **Import as:** pipe, box culvert, structure, exact 3D solid, or skip.
   - **Civil 3D part:** the size it will use.
   - **Network:** where the part will go. **→** means a network already in your drawing, **＋** a new one. Right-click rows and choose **Send to network…** to change it.
   - **In drawing:** marks parts that are already drawn. They are left out.
4. Use **Preview in drawing** to see the parts first, then **Import ticked…**. A confirmation lists everything that will be added. One **UNDO** removes the whole import.

The first time it opens, the tool asks how to make sizes your catalog does not have. **Method A** (recommended) adds four C3D Pro any-size families. Civil 3D then needs one restart to show them.

In **Options**, tick **Connect pipes to the structures they end in** if you want imported pipes connected to manholes.

## Surface from Solids

1. **C3D Pro Tools → Surface from Solids**.
2. Select the 3D solids and meshes.
3. Choose **Top**, **Bottom** or **Both**.
4. Press Enter to accept the suggested name and style, or type your own.

The command line reports how many triangles were made. It warns you if objects overlap in plan, or if a solid face is curved.

## Rebuild Network

1. **C3D Pro Tools → Rebuild Network**. The networks are listed, and data-shortcut references are ticked.
2. **Check** shows what each network will become. Nothing is changed.
3. **Rebuild** draws it as "*name* - REBUILT". The original is only read, and one **UNDO** removes the rebuild.

If the drawing uses the US Imperial part catalog, set the pipe network catalog to Metric first. The report tells you when this applies.

## Feedback

Use **C3D Pro Tools → Send Feedback**. Tell us what worked, what did not, and what you would like next. For a problem, attach the log file: **About** shows where it is.

## Please note

The tools are free and provided as is, without warranty. They change only what they report, always in one UNDO step. Check the result before you save, and try a tool on a copy of your drawing first.
