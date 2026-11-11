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

            RibbonPanelSource panelSource = new RibbonPanelSource { Title = "Pro Tools" };
            RibbonPanel panel = new RibbonPanel { Source = panelSource };
            customTab.Panels.Add(panel);

            // --- 0. Workspace Palette Button ---
            RibbonButton workspaceButton = new RibbonButton
            {
                Text = "Workspace\nPalette",
                ShowText = true,
                Size = RibbonItemSize.Large,
                Orientation = System.Windows.Controls.Orientation.Vertical,
                CommandParameter = "C3DProWorkspace ",
                CommandHandler = new RibbonCommandHandler()
            };
            panelSource.Items.Add(workspaceButton);
            panelSource.Items.Add(new RibbonSeparator());

            // --- 1. Style Manager Button ---
            RibbonButton styleManagerButton = new RibbonButton
            {
                Text = "Style\nManager",
                ShowText = true,
                Size = RibbonItemSize.Large,
                Orientation = System.Windows.Controls.Orientation.Vertical,
                CommandParameter = "StyleManager ",
                CommandHandler = new RibbonCommandHandler()
            };
            panelSource.Items.Add(styleManagerButton);

            // --- 2. Navisworks Pipe Importer Button ---
            RibbonButton navisButton = new RibbonButton
            {
                Text = "Navisworks\nImporter",
                ShowText = true,
                Size = RibbonItemSize.Large,
                Orientation = System.Windows.Controls.Orientation.Vertical,
                CommandParameter = "ImportNavisPipe ",
                CommandHandler = new RibbonCommandHandler()
            };
            panelSource.Items.Add(new RibbonSeparator());
            panelSource.Items.Add(navisButton);

            // --- 3. Network Editor Button ---
            RibbonButton networkEditorButton = new RibbonButton
            {
                Text = "Network\nEditor",
                ShowText = true,
                Size = RibbonItemSize.Large,
                Orientation = System.Windows.Controls.Orientation.Vertical,
                CommandParameter = "NetworkEditor ",
                CommandHandler = new RibbonCommandHandler()
            };
            panelSource.Items.Add(new RibbonSeparator());
            panelSource.Items.Add(networkEditorButton);

            // --- 3a. Network Organizer Button ---
            RibbonButton organizerButton = new RibbonButton
            {
                Text = "Network\nOrganizer",
                ShowText = true,
                Size = RibbonItemSize.Large,
                Orientation = System.Windows.Controls.Orientation.Vertical,
                CommandParameter = "NetworkOrganizer ",
                CommandHandler = new RibbonCommandHandler()
            };
            panelSource.Items.Add(new RibbonSeparator());
            panelSource.Items.Add(organizerButton);

            // --- 3b. Networks from Feature Lines Button ---
            RibbonButton fromLinesButton = new RibbonButton
            {
                Text = "Networks from\nFeature Lines",
                ShowText = true,
                Size = RibbonItemSize.Large,
                Orientation = System.Windows.Controls.Orientation.Vertical,
                CommandParameter = "NetworkFromFeatureLines ",
                CommandHandler = new RibbonCommandHandler()
            };
            panelSource.Items.Add(new RibbonSeparator());
            panelSource.Items.Add(fromLinesButton);

            // --- 4. Profile & Cover Designer Button ---
            RibbonButton designerButton = new RibbonButton
            {
                Text = "Profile &\nCover Designer",
                ShowText = true,
                Size = RibbonItemSize.Large,
                Orientation = System.Windows.Controls.Orientation.Vertical,
                CommandParameter = "ProfileCoverDesigner ",
                CommandHandler = new RibbonCommandHandler()
            };
            panelSource.Items.Add(new RibbonSeparator());
            panelSource.Items.Add(designerButton);

            // --- 4a. Profile View Tools Button ---
            RibbonButton viewToolsButton = new RibbonButton
            {
                Text = "Profile View\nTools",
                ShowText = true,
                Size = RibbonItemSize.Large,
                Orientation = System.Windows.Controls.Orientation.Vertical,
                CommandParameter = "ProfileViewTools ",
                CommandHandler = new RibbonCommandHandler()
            };
            panelSource.Items.Add(new RibbonSeparator());
            panelSource.Items.Add(viewToolsButton);

            // --- 4b. Annotation Studio Button ---
            RibbonButton annotationButton = new RibbonButton
            {
                Text = "Annotation\nStudio",
                ShowText = true,
                Size = RibbonItemSize.Large,
                Orientation = System.Windows.Controls.Orientation.Vertical,
                CommandParameter = "AnnotationStudio ",
                CommandHandler = new RibbonCommandHandler()
            };
            panelSource.Items.Add(new RibbonSeparator());
            panelSource.Items.Add(annotationButton);

            // --- 4c. Sheet Manager Button ---
            RibbonButton sheetsButton = new RibbonButton
            {
                Text = "Sheet\nManager",
                ShowText = true,
                Size = RibbonItemSize.Large,
                Orientation = System.Windows.Controls.Orientation.Vertical,
                CommandParameter = "SheetManager ",
                CommandHandler = new RibbonCommandHandler()
            };
            panelSource.Items.Add(new RibbonSeparator());
            panelSource.Items.Add(sheetsButton);

            // --- 5. C3D Drawing Cleaner Button ---
            RibbonButton cleanerButton = new RibbonButton
            {
                Text = "C3D Drawing\nCleaner",
                ShowText = true,
                Size = RibbonItemSize.Large,
                Orientation = System.Windows.Controls.Orientation.Vertical,
                CommandParameter = "C3DDrawingCleaner ",
                CommandHandler = new RibbonCommandHandler()
            };
            panelSource.Items.Add(new RibbonSeparator());
            panelSource.Items.Add(cleanerButton);

            // --- 6. Batch Tools panel: profiles by surface, TOP profiles on ducts, label style values ---
            RibbonPanelSource batchSource = new RibbonPanelSource { Title = "Batch Tools" };
            customTab.Panels.Add(new RibbonPanel { Source = batchSource });
            batchSource.Items.Add(Large("Profile\nManager", "PROFILEMGR ", "Profiles of every alignment by source surface: delete by surface, create surface profiles on all alignments."));
            batchSource.Items.Add(new RibbonSeparator());
            batchSource.Items.Add(Large("Profile Views\n& TOP Fit", "PVMGR ", "Every profile view with its TOP profile: fit TOP profiles to the duct tops, create the missing ones."));
            batchSource.Items.Add(new RibbonSeparator());
            batchSource.Items.Add(Large("Label Style\nValues", "STYLEMGR ", "Batch-set label style values (text heights, arrowheads, ...) for styles by category or from labels you pick."));
            batchSource.Items.Add(new RibbonSeparator());
            batchSource.Items.Add(Large("Profile\nDIMs", "PROFILEDIMS ", "Draw cover DIMs (duct top to proposed surface, at structure faces and grade breaks) and clearance DIMs (duct to crossing pipes) in profile views."));

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