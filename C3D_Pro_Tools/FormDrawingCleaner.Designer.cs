namespace C3DProTools
{
    partial class FormDrawingCleaner
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        private void InitializeComponent()
        {
            this.lblHeader = new System.Windows.Forms.Label();
            this.grpCivil = new System.Windows.Forms.GroupBox();
            this.chkCorridors = new System.Windows.Forms.CheckBox();
            this.chkSurfaces = new System.Windows.Forms.CheckBox();
            this.chkAlignments = new System.Windows.Forms.CheckBox();
            this.chkCogoPoints = new System.Windows.Forms.CheckBox();
            this.chkFeatureLines = new System.Windows.Forms.CheckBox();
            this.chkParcels = new System.Windows.Forms.CheckBox();
            this.grpNets = new System.Windows.Forms.GroupBox();
            this.lblGravity = new System.Windows.Forms.Label();
            this.clbGravityNets = new System.Windows.Forms.CheckedListBox();
            this.lblPressure = new System.Windows.Forms.Label();
            this.clbPressureNets = new System.Windows.Forms.CheckedListBox();
            this.grpAcad = new System.Windows.Forms.GroupBox();
            this.chkLabelsTables = new System.Windows.Forms.CheckBox();
            this.chkAnnotations = new System.Windows.Forms.CheckBox();
            this.chkBlocksXrefs = new System.Windows.Forms.CheckBox();
            this.chkCadGeometry = new System.Windows.Forms.CheckBox();
            this.chkSheets = new System.Windows.Forms.CheckBox();
            this.chkDeepPurge = new System.Windows.Forms.CheckBox();
            this.lblXrefs = new System.Windows.Forms.Label();
            this.cbXrefs = new System.Windows.Forms.ComboBox();
            this.lblDataRefs = new System.Windows.Forms.Label();
            this.cbDataRefs = new System.Windows.Forms.ComboBox();
            this.btnPresetNetworksOnly = new System.Windows.Forms.Button();
            this.btnSelectAll = new System.Windows.Forms.Button();
            this.btnClearAll = new System.Windows.Forms.Button();
            this.grpSafety = new System.Windows.Forms.GroupBox();
            this.chkSaveCopy = new System.Windows.Forms.CheckBox();
            this.txtSavePath = new System.Windows.Forms.TextBox();
            this.btnBrowse = new System.Windows.Forms.Button();
            this.btnExecute = new System.Windows.Forms.Button();
            this.btnCancel = new System.Windows.Forms.Button();
            this.grpCivil.SuspendLayout();
            this.grpNets.SuspendLayout();
            this.grpAcad.SuspendLayout();
            this.grpSafety.SuspendLayout();
            this.SuspendLayout();
            // 
            // lblHeader
            // 
            this.lblHeader.ForeColor = System.Drawing.Color.DarkSlateGray;
            this.lblHeader.Location = new System.Drawing.Point(20, 10);
            this.lblHeader.Name = "lblHeader";
            this.lblHeader.Size = new System.Drawing.Size(640, 26);
            this.lblHeader.TabIndex = 0;
            this.lblHeader.Text = "1. Review detected elements -> 2. Select items to delete -> 3. Execute Delete & Purge -> 4. Save Copy.";
            // 
            // grpCivil
            // 
            this.grpCivil.Controls.Add(this.chkCorridors);
            this.grpCivil.Controls.Add(this.chkSurfaces);
            this.grpCivil.Controls.Add(this.chkAlignments);
            this.grpCivil.Controls.Add(this.chkCogoPoints);
            this.grpCivil.Controls.Add(this.chkFeatureLines);
            this.grpCivil.Controls.Add(this.chkParcels);
            this.grpCivil.Location = new System.Drawing.Point(20, 38);
            this.grpCivil.Name = "grpCivil";
            this.grpCivil.Size = new System.Drawing.Size(640, 105);
            this.grpCivil.TabIndex = 1;
            this.grpCivil.TabStop = false;
            this.grpCivil.Text = "1. Civil 3D Model Elements to Delete";
            // 
            // chkCorridors
            // 
            this.chkCorridors.Checked = true;
            this.chkCorridors.CheckState = System.Windows.Forms.CheckState.Checked;
            this.chkCorridors.Location = new System.Drawing.Point(15, 22);
            this.chkCorridors.Name = "chkCorridors";
            this.chkCorridors.Size = new System.Drawing.Size(295, 22);
            this.chkCorridors.TabIndex = 0;
            this.chkCorridors.Text = "Corridors & Assemblies [0]";
            // 
            // chkSurfaces
            // 
            this.chkSurfaces.Checked = true;
            this.chkSurfaces.CheckState = System.Windows.Forms.CheckState.Checked;
            this.chkSurfaces.Location = new System.Drawing.Point(15, 48);
            this.chkSurfaces.Name = "chkSurfaces";
            this.chkSurfaces.Size = new System.Drawing.Size(295, 22);
            this.chkSurfaces.TabIndex = 1;
            this.chkSurfaces.Text = "Surfaces (TIN / Grid / Volume) [0]";
            // 
            // chkAlignments
            // 
            this.chkAlignments.Checked = true;
            this.chkAlignments.CheckState = System.Windows.Forms.CheckState.Checked;
            this.chkAlignments.Location = new System.Drawing.Point(15, 74);
            this.chkAlignments.Name = "chkAlignments";
            this.chkAlignments.Size = new System.Drawing.Size(295, 22);
            this.chkAlignments.TabIndex = 2;
            this.chkAlignments.Text = "Alignments, Profiles, Views & Frames [0]";
            // 
            // chkCogoPoints
            // 
            this.chkCogoPoints.Checked = true;
            this.chkCogoPoints.CheckState = System.Windows.Forms.CheckState.Checked;
            this.chkCogoPoints.Location = new System.Drawing.Point(325, 22);
            this.chkCogoPoints.Name = "chkCogoPoints";
            this.chkCogoPoints.Size = new System.Drawing.Size(295, 22);
            this.chkCogoPoints.TabIndex = 3;
            this.chkCogoPoints.Text = "COGO Points & Point Groups [0]";
            // 
            // chkFeatureLines
            // 
            this.chkFeatureLines.Checked = true;
            this.chkFeatureLines.CheckState = System.Windows.Forms.CheckState.Checked;
            this.chkFeatureLines.Location = new System.Drawing.Point(325, 48);
            this.chkFeatureLines.Name = "chkFeatureLines";
            this.chkFeatureLines.Size = new System.Drawing.Size(295, 22);
            this.chkFeatureLines.TabIndex = 4;
            this.chkFeatureLines.Text = "Feature Lines, Gradings & Sites [0]";
            // 
            // chkParcels
            // 
            this.chkParcels.Checked = true;
            this.chkParcels.CheckState = System.Windows.Forms.CheckState.Checked;
            this.chkParcels.Location = new System.Drawing.Point(325, 74);
            this.chkParcels.Name = "chkParcels";
            this.chkParcels.Size = new System.Drawing.Size(295, 22);
            this.chkParcels.TabIndex = 5;
            this.chkParcels.Text = "Parcels & Catchments [0]";
            // 
            // grpNets
            // 
            this.grpNets.Controls.Add(this.lblGravity);
            this.grpNets.Controls.Add(this.clbGravityNets);
            this.grpNets.Controls.Add(this.lblPressure);
            this.grpNets.Controls.Add(this.clbPressureNets);
            this.grpNets.Location = new System.Drawing.Point(20, 150);
            this.grpNets.Name = "grpNets";
            this.grpNets.Size = new System.Drawing.Size(640, 165);
            this.grpNets.TabIndex = 2;
            this.grpNets.TabStop = false;
            this.grpNets.Text = "2. Specific Pipe Networks to DELETE (Leave UNCHECKED to KEEP)";
            // 
            // lblGravity
            // 
            this.lblGravity.Location = new System.Drawing.Point(15, 20);
            this.lblGravity.Name = "lblGravity";
            this.lblGravity.Size = new System.Drawing.Size(290, 18);
            this.lblGravity.TabIndex = 0;
            this.lblGravity.Text = "Gravity Networks (Checked = Delete):";
            // 
            // clbGravityNets
            // 
            this.clbGravityNets.CheckOnClick = true;
            this.clbGravityNets.Location = new System.Drawing.Point(15, 40);
            this.clbGravityNets.Name = "clbGravityNets";
            this.clbGravityNets.Size = new System.Drawing.Size(295, 112);
            this.clbGravityNets.TabIndex = 1;
            // 
            // lblPressure
            // 
            this.lblPressure.Location = new System.Drawing.Point(325, 20);
            this.lblPressure.Name = "lblPressure";
            this.lblPressure.Size = new System.Drawing.Size(290, 18);
            this.lblPressure.TabIndex = 2;
            this.lblPressure.Text = "Pressure Networks (Checked = Delete):";
            // 
            // clbPressureNets
            // 
            this.clbPressureNets.CheckOnClick = true;
            this.clbPressureNets.Location = new System.Drawing.Point(325, 40);
            this.clbPressureNets.Name = "clbPressureNets";
            this.clbPressureNets.Size = new System.Drawing.Size(295, 112);
            this.clbPressureNets.TabIndex = 3;
            // 
            // grpAcad
            // 
            this.grpAcad.Controls.Add(this.chkLabelsTables);
            this.grpAcad.Controls.Add(this.chkAnnotations);
            this.grpAcad.Controls.Add(this.chkBlocksXrefs);
            this.grpAcad.Controls.Add(this.chkCadGeometry);
            this.grpAcad.Controls.Add(this.chkSheets);
            this.grpAcad.Controls.Add(this.chkDeepPurge);
            this.grpAcad.Controls.Add(this.lblXrefs);
            this.grpAcad.Controls.Add(this.cbXrefs);
            this.grpAcad.Controls.Add(this.lblDataRefs);
            this.grpAcad.Controls.Add(this.cbDataRefs);
            this.grpAcad.Location = new System.Drawing.Point(20, 322);
            this.grpAcad.Name = "grpAcad";
            this.grpAcad.Size = new System.Drawing.Size(640, 158);
            this.grpAcad.TabIndex = 3;
            this.grpAcad.TabStop = false;
            this.grpAcad.Text = "3. Labels, Tables, AutoCAD Objects, Blocks, Sheets & Deep Purge";
            // 
            // chkLabelsTables
            // 
            this.chkLabelsTables.Checked = true;
            this.chkLabelsTables.CheckState = System.Windows.Forms.CheckState.Checked;
            this.chkLabelsTables.Location = new System.Drawing.Point(15, 22);
            this.chkLabelsTables.Name = "chkLabelsTables";
            this.chkLabelsTables.Size = new System.Drawing.Size(295, 22);
            this.chkLabelsTables.TabIndex = 0;
            this.chkLabelsTables.Text = "All Civil 3D Labels & C3D Tables [0]";
            // 
            // chkAnnotations
            // 
            this.chkAnnotations.Checked = true;
            this.chkAnnotations.CheckState = System.Windows.Forms.CheckState.Checked;
            this.chkAnnotations.Location = new System.Drawing.Point(325, 22);
            this.chkAnnotations.Name = "chkAnnotations";
            this.chkAnnotations.Size = new System.Drawing.Size(295, 22);
            this.chkAnnotations.TabIndex = 1;
            this.chkAnnotations.Text = "CAD Texts, Dimensions, Leaders & Tables [0]";
            // 
            // chkBlocksXrefs
            // 
            this.chkBlocksXrefs.Checked = true;
            this.chkBlocksXrefs.CheckState = System.Windows.Forms.CheckState.Checked;
            this.chkBlocksXrefs.Location = new System.Drawing.Point(15, 48);
            this.chkBlocksXrefs.Name = "chkBlocksXrefs";
            this.chkBlocksXrefs.Size = new System.Drawing.Size(295, 22);
            this.chkBlocksXrefs.TabIndex = 2;
            this.chkBlocksXrefs.Text = "Blocks, Images, PDFs & Xrefs [0]";
            // 
            // chkCadGeometry
            // 
            this.chkCadGeometry.Checked = true;
            this.chkCadGeometry.CheckState = System.Windows.Forms.CheckState.Checked;
            this.chkCadGeometry.Location = new System.Drawing.Point(325, 48);
            this.chkCadGeometry.Name = "chkCadGeometry";
            this.chkCadGeometry.Size = new System.Drawing.Size(295, 22);
            this.chkCadGeometry.TabIndex = 3;
            this.chkCadGeometry.Text = "All 2D/3D CAD Lines, Hatches & Solids [0]";
            // 
            // chkSheets
            // 
            this.chkSheets.Checked = true;
            this.chkSheets.CheckState = System.Windows.Forms.CheckState.Checked;
            this.chkSheets.Location = new System.Drawing.Point(15, 74);
            this.chkSheets.Name = "chkSheets";
            this.chkSheets.Size = new System.Drawing.Size(295, 22);
            this.chkSheets.TabIndex = 4;
            this.chkSheets.Text = "Paper Space Layout Sheets [0]";
            // 
            // chkDeepPurge
            // 
            this.chkDeepPurge.Checked = true;
            this.chkDeepPurge.CheckState = System.Windows.Forms.CheckState.Checked;
            this.chkDeepPurge.Font = new System.Drawing.Font("Segoe UI", 9F, System.Drawing.FontStyle.Bold);
            this.chkDeepPurge.Location = new System.Drawing.Point(15, 100);
            this.chkDeepPurge.Name = "chkDeepPurge";
            this.chkDeepPurge.Size = new System.Drawing.Size(605, 22);
            this.chkDeepPurge.TabIndex = 5;
            this.chkDeepPurge.Text = "Purge Everything (Unused Blocks, Layers, Linetypes, Styles, Materials & RegApps)";
            //
            // lblXrefs
            //
            this.lblXrefs.Location = new System.Drawing.Point(15, 129);
            this.lblXrefs.Name = "lblXrefs";
            this.lblXrefs.Size = new System.Drawing.Size(70, 18);
            this.lblXrefs.Text = "Xrefs:";
            //
            // cbXrefs
            //
            this.cbXrefs.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cbXrefs.Location = new System.Drawing.Point(85, 126);
            this.cbXrefs.Name = "cbXrefs";
            this.cbXrefs.Size = new System.Drawing.Size(225, 23);
            this.cbXrefs.TabIndex = 6;
            //
            // lblDataRefs
            //
            this.lblDataRefs.Location = new System.Drawing.Point(325, 129);
            this.lblDataRefs.Name = "lblDataRefs";
            this.lblDataRefs.Size = new System.Drawing.Size(100, 18);
            this.lblDataRefs.Text = "Data shortcuts:";
            //
            // cbDataRefs
            //
            this.cbDataRefs.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cbDataRefs.Location = new System.Drawing.Point(425, 126);
            this.cbDataRefs.Name = "cbDataRefs";
            this.cbDataRefs.Size = new System.Drawing.Size(200, 23);
            this.cbDataRefs.TabIndex = 7;
            // 
            // btnPresetNetworksOnly
            // 
            this.btnPresetNetworksOnly.Font = new System.Drawing.Font("Segoe UI", 9F, System.Drawing.FontStyle.Bold);
            this.btnPresetNetworksOnly.Location = new System.Drawing.Point(20, 488);
            this.btnPresetNetworksOnly.Name = "btnPresetNetworksOnly";
            this.btnPresetNetworksOnly.Size = new System.Drawing.Size(310, 30);
            this.btnPresetNetworksOnly.TabIndex = 4;
            this.btnPresetNetworksOnly.Text = "★ Mode: Keep Networks Only (Delete All Else)";
            this.btnPresetNetworksOnly.Click += new System.EventHandler(this.btnPresetNetworksOnly_Click);
            // 
            // btnSelectAll
            // 
            this.btnSelectAll.Location = new System.Drawing.Point(340, 488);
            this.btnSelectAll.Name = "btnSelectAll";
            this.btnSelectAll.Size = new System.Drawing.Size(155, 30);
            this.btnSelectAll.TabIndex = 5;
            this.btnSelectAll.Text = "Select Everything";
            this.btnSelectAll.Click += new System.EventHandler(this.btnSelectAll_Click);
            // 
            // btnClearAll
            // 
            this.btnClearAll.Location = new System.Drawing.Point(505, 488);
            this.btnClearAll.Name = "btnClearAll";
            this.btnClearAll.Size = new System.Drawing.Size(155, 30);
            this.btnClearAll.TabIndex = 6;
            this.btnClearAll.Text = "Clear All";
            this.btnClearAll.Click += new System.EventHandler(this.btnClearAll_Click);
            // 
            // grpSafety
            // 
            this.grpSafety.Controls.Add(this.chkSaveCopy);
            this.grpSafety.Controls.Add(this.txtSavePath);
            this.grpSafety.Controls.Add(this.btnBrowse);
            this.grpSafety.Location = new System.Drawing.Point(20, 526);
            this.grpSafety.Name = "grpSafety";
            this.grpSafety.Size = new System.Drawing.Size(640, 88);
            this.grpSafety.TabIndex = 7;
            this.grpSafety.TabStop = false;
            this.grpSafety.Text = "4. Save Cleaned Drawing Copy (Executed After Delete & Purge)";
            // 
            // chkSaveCopy
            // 
            this.chkSaveCopy.Checked = true;
            this.chkSaveCopy.CheckState = System.Windows.Forms.CheckState.Checked;
            this.chkSaveCopy.Font = new System.Drawing.Font("Segoe UI", 9F, System.Drawing.FontStyle.Bold);
            this.chkSaveCopy.Location = new System.Drawing.Point(15, 20);
            this.chkSaveCopy.Name = "chkSaveCopy";
            this.chkSaveCopy.Size = new System.Drawing.Size(605, 22);
            this.chkSaveCopy.TabIndex = 0;
            this.chkSaveCopy.Text = "Save cleaned result as a new drawing copy after delete and purge";
            this.chkSaveCopy.CheckedChanged += new System.EventHandler(this.chkSaveCopy_CheckedChanged);
            // 
            // txtSavePath
            // 
            this.txtSavePath.Location = new System.Drawing.Point(15, 48);
            this.txtSavePath.Name = "txtSavePath";
            this.txtSavePath.ReadOnly = true;
            this.txtSavePath.Size = new System.Drawing.Size(505, 23);
            this.txtSavePath.TabIndex = 1;
            // 
            // btnBrowse
            // 
            this.btnBrowse.Location = new System.Drawing.Point(530, 46);
            this.btnBrowse.Name = "btnBrowse";
            this.btnBrowse.Size = new System.Drawing.Size(95, 26);
            this.btnBrowse.TabIndex = 2;
            this.btnBrowse.Text = "Browse...";
            this.btnBrowse.Click += new System.EventHandler(this.btnBrowse_Click);
            // 
            // btnExecute
            // 
            this.btnExecute.Font = new System.Drawing.Font("Segoe UI", 9.5F, System.Drawing.FontStyle.Bold);
            this.btnExecute.ForeColor = System.Drawing.Color.DarkRed;
            this.btnExecute.Location = new System.Drawing.Point(290, 626);
            this.btnExecute.Name = "btnExecute";
            this.btnExecute.Size = new System.Drawing.Size(210, 38);
            this.btnExecute.TabIndex = 8;
            this.btnExecute.Text = "Clean, Purge & Save";
            this.btnExecute.Click += new System.EventHandler(this.BtnExecute_Click);
            // 
            // btnCancel
            // 
            this.btnCancel.Location = new System.Drawing.Point(510, 626);
            this.btnCancel.Name = "btnCancel";
            this.btnCancel.Size = new System.Drawing.Size(150, 38);
            this.btnCancel.TabIndex = 9;
            this.btnCancel.Text = "Cancel";
            this.btnCancel.Click += new System.EventHandler(this.btnCancel_Click);
            // 
            // FormDrawingCleaner
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(680, 678);
            this.Controls.Add(this.lblHeader);
            this.Controls.Add(this.grpCivil);
            this.Controls.Add(this.grpNets);
            this.Controls.Add(this.grpAcad);
            this.Controls.Add(this.btnPresetNetworksOnly);
            this.Controls.Add(this.btnSelectAll);
            this.Controls.Add(this.btnClearAll);
            this.Controls.Add(this.grpSafety);
            this.Controls.Add(this.btnExecute);
            this.Controls.Add(this.btnCancel);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Name = "FormDrawingCleaner";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "C3D Drawing Cleaner";
            this.grpCivil.ResumeLayout(false);
            this.grpNets.ResumeLayout(false);
            this.grpAcad.ResumeLayout(false);
            this.grpSafety.ResumeLayout(false);
            this.grpSafety.PerformLayout();
            this.ResumeLayout(false);
        }

        #endregion

        private System.Windows.Forms.Label lblHeader;
        private System.Windows.Forms.GroupBox grpCivil;
        private System.Windows.Forms.CheckBox chkCorridors;
        private System.Windows.Forms.CheckBox chkSurfaces;
        private System.Windows.Forms.CheckBox chkAlignments;
        private System.Windows.Forms.CheckBox chkCogoPoints;
        private System.Windows.Forms.CheckBox chkFeatureLines;
        private System.Windows.Forms.CheckBox chkParcels;
        private System.Windows.Forms.GroupBox grpNets;
        private System.Windows.Forms.Label lblGravity;
        private System.Windows.Forms.CheckedListBox clbGravityNets;
        private System.Windows.Forms.Label lblPressure;
        private System.Windows.Forms.CheckedListBox clbPressureNets;
        private System.Windows.Forms.GroupBox grpAcad;
        private System.Windows.Forms.CheckBox chkLabelsTables;
        private System.Windows.Forms.CheckBox chkAnnotations;
        private System.Windows.Forms.CheckBox chkBlocksXrefs;
        private System.Windows.Forms.CheckBox chkCadGeometry;
        private System.Windows.Forms.CheckBox chkSheets;
        private System.Windows.Forms.CheckBox chkDeepPurge;
        private System.Windows.Forms.Label lblXrefs;
        private System.Windows.Forms.ComboBox cbXrefs;
        private System.Windows.Forms.Label lblDataRefs;
        private System.Windows.Forms.ComboBox cbDataRefs;
        private System.Windows.Forms.Button btnPresetNetworksOnly;
        private System.Windows.Forms.Button btnSelectAll;
        private System.Windows.Forms.Button btnClearAll;
        private System.Windows.Forms.GroupBox grpSafety;
        private System.Windows.Forms.CheckBox chkSaveCopy;
        private System.Windows.Forms.TextBox txtSavePath;
        private System.Windows.Forms.Button btnBrowse;
        private System.Windows.Forms.Button btnExecute;
        private System.Windows.Forms.Button btnCancel;
    }
}