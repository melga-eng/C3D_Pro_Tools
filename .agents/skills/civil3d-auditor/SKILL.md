---
name: civil3d-auditor
description: >-
  Auditing, issue spotting, and reviewing Autodesk Civil 3D and AutoCAD .NET API code.
  Use when analyzing C# Civil 3D / AutoCAD code for transaction leaks, document locking
  issues, COM vs .NET pitfalls, geometry errors, pipe network/conduit logic, style
  management, and .NET 10 / Civil 3D 2026 compatibility.
---

# Civil 3D & AutoCAD .NET API Code Audit Guide

This skill provides an authoritative checklist and audit protocol for reviewing Civil 3D and AutoCAD C# .NET code (including Civil 3D 2026 on modern .NET).

---

## 1. Document & Transaction Integrity

### A. Document Locking (Crucial in Modeless Forms & Async)
* **Rule:** Any operation modifying the AutoCAD/Civil 3D database initiated from a Modeless Form (WPF / WinForms), Ribbon button, or external event **must** obtain a `DocumentLock`.
* **Pattern:**
  ```csharp
  Document doc = Application.DocumentManager.MdiActiveDocument;
  using (DocumentLock docLock = doc.LockDocument())
  {
      using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
      {
          // DB modifications
          tr.Commit();
      }
  }
  ```
* **Red Flag:** Calling `doc.Database.TransactionManager.StartTransaction()` from a dialog button click or worker thread without `doc.LockDocument()`. Results in `eLockViolation`.

### B. Transaction Lifecycles & Disposal
* **Rule:** All `Transaction` instances must be wrapped in `using (...)`.
* **Rule:** Never reuse an `ObjectId`'s opened entity instance after its transaction has committed or aborted. Always re-fetch via `tr.GetObject(id, OpenMode.ForRead/ForWrite)`.
* **Rule:** If an exception occurs, do not commit. Let the `using` block dispose/abort the transaction cleanly.

---

## 2. Civil 3D Object Traps

### A. CivilApplication & Database Binding
* Civil 3D objects (`CivilDocument`) must be retrieved through:
  ```csharp
  CivilDocument civilDoc = CivilApplication.ActiveDocument;
  ```
* In batch or multi-document workflows, verify whether `civilDoc.Database == doc.Database`.

### B. Pipe Networks & Pressure Networks
* **Gravity Pipes (`Network`, `Pipe`, `Structure`):**
  * Inverts, slopes, and diameters must be set through the official parameter accessors or `Pipe.SetEndpointAndInvert()`.
  * Ensure parts list (`PartsListId`) and part family (`PartFamilyId`) are valid in the active drawing's catalog before creating parts.
* **3D Solids vs Civil 3D Entities:**
  * Converting Navisworks or generic mesh/solid data to Civil 3D conduits requires valid coordinate system transformations and plane alignment. Check for proper WCS (`WorldCoordinateSystem`) vs UCS (`UserCoordinateSystem`) transformations.

### C. Styles & Dictionaries
* Check whether style names already exist in the style collection (`civilDoc.Styles.PipeStyles`, `AlignmentStyles`, etc.) before calling `.Add()`. Calling `.Add()` with an existing name throws an exception.

---

## 3. Windows Forms / UI Audit in Civil 3D

* **Modal vs Modeless:**
  * If the form is Modeless (`form.Show()`), it must lock the document for every command, or use `Application.ShowModelessDialog()`.
  * If the form is Modal (`Application.ShowModalDialog(form)`), the document context is suspended, but transactions still require proper scoping.
* **Event Handlers:**
  * Unsubscribe from AutoCAD document events (`DocumentActivated`, `DocumentToBeDestroyed`) when the form closes to prevent memory leaks and `AccessViolationException`.

---

## 4. Modern .NET (Civil 3D 2025/2026 & .NET 8/10) Traps

* **Assembly References:** Ensure `Copy Local = False` (or `<Private>False</Private>`) on all Autodesk assemblies (`accoremgd.dll`, `acdbmgd.dll`, `acmgd.dll`, `AeccDbMgd.dll`, `AeccAppMgd.dll`).
* **WinForms DPI & Threading:** Civil 3D 2026 runs on modern .NET Core/CLR. Interop with unmanaged COM APIs requires explicit type marshaling and STA thread discipline.
