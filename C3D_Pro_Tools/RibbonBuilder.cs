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

            // Two panels in the order of the work: model the networks, then produce the drawings. Every other tool stays a
            // typed command (PRODUCT_MAP.md lists them); the ribbon holds only what is used.
            RibbonPanelSource model = new RibbonPanelSource { Title = "Model" };
            customTab.Panels.Add(new RibbonPanel { Source = model });
            model.Items.Add(Large("Network\nEditor", "NetworkEditor ", "Split, merge, swap, fit and rim tools for pipe networks."));
            model.Items.Add(Large("Network\nOrganizer", "NetworkOrganizer ", "Organize parts and networks."));
            model.Items.Add(Large("Networks from\nFeature Lines", "NetworkFromFeatureLines ", "Build pipe networks from feature lines."));
            model.Items.Add(Large("Profile &\nCover Designer", "ProfileCoverDesigner ", "Set pipe levels along a route for cover, slope and clearances."));
            model.Items.Add(Large("Navisworks\nImporter", "ImportNavisPipe ", "Import pipes and structures from Navisworks."));

            RibbonPanelSource produce = new RibbonPanelSource { Title = "Produce" };
            customTab.Panels.Add(new RibbonPanel { Source = produce });
            produce.Items.Add(Large("Pro\nRun", "C3DPRORUN ", "Profile views (TOP fit, match, split, DIMs, pavement labels), network checks and cover, labels (add, arrange, reset, rename) in one window. Settings are saved in the drawing."));
            produce.Items.Add(Large("Annotation\nStudio", "AnnotationStudio ", "Add labels from label sets, arrange them inside the viewports, reset, rename parts."));
            produce.Items.Add(Large("Sheet\nManager", "SheetManager ", "List, rename, copy and create layouts by tile and part."));

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