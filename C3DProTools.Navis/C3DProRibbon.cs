using Autodesk.Navisworks.Api.Plugins;

namespace C3DProTools.Navis
{
    /// <summary>
    /// The C3D Pro tab: Clashes (Clash Grouper) and Civil 3D (the link to Civil 3D). The layout is Ribbon\C3DProRibbon.xaml,
    /// installed beside the add-in and in its en-US folder; the icons sit beside the add-in.
    /// </summary>
    [Plugin("C3DProTools.Ribbon", "C3DPro", DisplayName = "C3D Pro")]
    [RibbonLayout("C3DProRibbon.xaml")]
    [RibbonTab("ID_C3DPro_Tab", DisplayName = "C3D Pro")]
    [Command("ID_C3DPro_ClashGrouper", DisplayName = "Clash Grouper", Icon = "ClashGrouper_16.png", LargeIcon = "ClashGrouper_32.png",
        ToolTip = "Group clashes by connected elements, status and area; send them to Civil 3D")]
    [Command("ID_C3DPro_CivilLink", DisplayName = "Civil 3D Link", Icon = "CivilLink_16.png", LargeIcon = "CivilLink_32.png",
        ToolTip = "Shows whether Civil 3D's Navisworks Import can reach this Navisworks")]
    public class C3DProRibbon : CommandHandlerPlugin
    {
        public override int ExecuteCommand(string name, params string[] parameters)
        {
            switch (name)
            {
                case "ID_C3DPro_ClashGrouper": ClashGrouperCommand.ShowWindow(); break;
                case "ID_C3DPro_CivilLink": LinkStatusCommand.ShowStatus(); break;
            }
            return 0;
        }
    }
}
