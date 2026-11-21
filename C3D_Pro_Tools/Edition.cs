namespace C3DProTools
{
    /// <summary>
    /// Which installer this build is for. The same code builds both: the full edition (every tool, the one used on the
    /// project) and the public edition (built with -p:Edition=Public: only the tools released to everyone). In the public
    /// edition the other tools' commands are not registered at all, not just left off the ribbon.
    /// </summary>
    internal static class Edition
    {
#if PUBLIC
        public const bool IsPublic = true;
        public const string Name = "C3D Pro Tools";
#else
        public const bool IsPublic = false;
        public const string Name = "C3D Pro Tools (full)";
#endif
        /// <summary>The Google Form people send feedback with. Empty until the form exists: the button then says so.</summary>
        public const string FeedbackUrl = "";

        public static string Version
        {
            get
            {
                System.Version v = typeof(Edition).Assembly.GetName().Version;
                return v == null ? "" : $"{v.Major}.{v.Minor}.{v.Build}";
            }
        }
    }
}
