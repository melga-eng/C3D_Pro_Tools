using System;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Windows;

namespace C3DProTools
{
    public class RibbonBuilder : IExtensionApplication
    {
        private bool isBuilt = false;

        public void Initialize()
        {
            Features.Shared.SettingsStore.Install();
            if (ComponentManager.Ribbon == null)
            {
                ComponentManager.ItemInitialized += ComponentManager_ItemInitialized;
            }
            else
            {
                CreateRibbonButton();
            }
        }

        public void Terminate()
        {
            ComponentManager.ItemInitialized -= ComponentManager_ItemInitialized;
        }

        private void ComponentManager_ItemInitialized(object sender, RibbonItemEventArgs e)
        {
            if (ComponentManager.Ribbon != null)
            {
                CreateRibbonButton();
                ComponentManager.ItemInitialized -= ComponentManager_ItemInitialized;
            }
        }

        private void CreateRibbonButton()
        {
            if (isBuilt) return;

            RibbonControl ribbon = ComponentManager.Ribbon;
            if (ribbon == null) return;

            string myCustomTabId = "ID_TabC3DProTools";
            RibbonTab customTab = null;

            foreach (RibbonTab tab in ribbon.Tabs)
            {
                if (tab.Id == myCustomTabId)
                {
                    customTab = tab;
                    break;
                }
            }

            if (customTab == null)
            {
                customTab = new RibbonTab { Title = "C3D Pro Tools", Id = myCustomTabId };
                ribbon.Tabs.Add(customTab);
            }

            // Every tool, in panels by the kind of work: model the networks, profiles, labels and sheets, standards, and the
            // preset runs on the command line.
            RibbonPanelSource model = new RibbonPanelSource { Title = "Model" };
            customTab.Panels.Add(new RibbonPanel { Source = model });
            model.Items.Add(Large("Network\nEditor", "NetworkEditor ", "Split, merge, swap, fit and rim tools for pipe networks."));
            model.Items.Add(Large("Network\nOrganizer", "NetworkOrganizer ", "Organize parts and networks."));
            model.Items.Add(Large("Networks from\nFeature Lines", "NetworkFromFeatureLines ", "Build pipe networks from feature lines."));
            model.Items.Add(Large("Profile &\nCover Designer", "ProfileCoverDesigner ", "Set pipe levels along a route for cover, slope and clearances."));
            model.Items.Add(Large("Rebuild\nNetwork", "RebuildNetwork ", "Rebuild a network that cannot be edited (a data-shortcut reference whose source is gone) as an ordinary network: same structures, pipes, levels and connections. Check first, then Rebuild."));
            model.Items.Add(Large("Navisworks\nImporter", "ImportNavisPipe ", "Import pipes and structures from Navisworks."));

            RibbonPanelSource profiles = new RibbonPanelSource { Title = "Profiles" };
            customTab.Panels.Add(new RibbonPanel { Source = profiles });
            profiles.Items.Add(Large("Profile View\nTools", "ProfileViewTools ", "Match one profile view's style, bands and profiles to others; split long views."));
            profiles.Items.Add(Large("Profile\nManager", "PROFILEMGR ", "Profiles of every alignment by source surface: delete by surface, create surface profiles on all alignments."));
            profiles.Items.Add(Large("Profile Views\n& TOP Fit", "PVMGR ", "Every profile view with its TOP profile: fit TOP profiles to the duct tops, create the missing ones."));
            profiles.Items.Add(Large("Profile\nDIMs", "PROFILEDIMS ", "Cover DIMs (duct top to proposed surface) and clearance DIMs (duct to crossing pipes) in profile views."));

            RibbonPanelSource produce = new RibbonPanelSource { Title = "Produce" };
            customTab.Panels.Add(new RibbonPanel { Source = produce });
            produce.Items.Add(Large("Pro\nRun", "C3DPRORUN ", "Profile views (TOP fit, match, split, DIMs, pavement labels), network checks and cover, labels (add, arrange, reset, rename) in one window. Settings are saved in the drawing."));
            produce.Items.Add(Large("Annotation\nStudio", "AnnotationStudio ", "Add labels from label sets, arrange them inside the viewports, reset, rename parts."));
            produce.Items.Add(Large("Sheet\nManager", "SheetManager ", "List, rename, copy and create layouts by tile and part."));
            produce.Items.Add(Large("CAD\nSheets", "C3DPROCAD ", "One AutoCAD 2018 drawing per chosen sheet, named after it: labels exploded as each sheet shows them, that sheet only, unused items purged. Runs in the background on a copy; your drawing is not changed."));
            produce.Items.Add(Large("Workspace\nPalette", "C3DProWorkspace ", "Docked panel for building sheets."));

            RibbonPanelSource standards = new RibbonPanelSource { Title = "Standards" };
            customTab.Panels.Add(new RibbonPanel { Source = standards });
            standards.Items.Add(Large("Style\nManager", "StyleManager ", "Find unused, duplicate and non-standard styles; merge, replace, purge."));
            standards.Items.Add(Large("Label Style\nValues", "STYLEMGR ", "Batch-set label style values (text heights, arrowheads, ...) for styles by category or from labels you pick."));
            standards.Items.Add(Large("Network\nLayers", "NetworkLayers ", "Pipes and structures on the layer of their type (from their description: what Navisworks shows), labels on their labelling layer; tile label layers left alone. Check, untick, Fix: one UNDO."));
            standards.Items.Add(Large("Drawing\nStandards", "DrawingStandards ", "The drawing checked against the KSIA CAD standard: units, coordinate system, objects on 0, layer names and line weights, fonts, dimensions, viewport scales, xref paths, sheets, unused items. Fixes units and xref paths."));
            standards.Items.Add(Large("Drawing\nCleaner", "C3DDrawingCleaner ", "Save a cleaned copy of the drawing for submittal."));

            RibbonPanelSource runs = new RibbonPanelSource { Title = "Command Runs" };
            customTab.Panels.Add(new RibbonPanel { Source = runs });
            runs.Items.Add(Large("Profile Views\nRun", "C3DPROPV ", "The Pro Run profile view steps on the command line, for the alignments you type."));
            runs.Items.Add(Large("Networks\nRun", "C3DPRONET ", "The Pro Run network steps on the command line, for the networks you type."));
            runs.Items.Add(Large("Labels\nRun", "C3DPROLBL ", "The Pro Run label steps on the command line, for the layouts you type."));

            isBuilt = true;
        }

        private static RibbonButton Large(string text, string command, string tip) => new RibbonButton
        {
            Text = text,
            ShowText = true,
            Size = RibbonItemSize.Large,
            Orientation = System.Windows.Controls.Orientation.Vertical,
            CommandParameter = command,
            ToolTip = tip,
            CommandHandler = new RibbonCommandHandler()
        };
    }

    public class RibbonCommandHandler : System.Windows.Input.ICommand
    {
        public event EventHandler CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object parameter) => true;

        public void Execute(object parameter)
        {
            if (parameter is RibbonButton btn && btn.CommandParameter != null)
            {
                var doc = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
                if (doc == null) return;
                doc.SendStringToExecute(btn.CommandParameter.ToString(), true, false, true);
            }
        }
    }
}