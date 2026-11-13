using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;

namespace C3DProTools.Features.Labels
{
    /// <summary>
    /// While labels are measured for a sheet: the Model tab current and its view turned like the sheet's viewport, so Civil 3D
    /// draws each label as that viewport shows it. Disposing puts the tab and the view back as they were.
    /// </summary>
    internal sealed class ViewTurn : IDisposable
    {
        private readonly Document _doc;
        private readonly string _layout;
        private readonly double _twist;
        private bool _turned;

        public ViewTurn(Document doc)
        {
            _doc = doc;
            _layout = LayoutManager.Current.CurrentLayout;
            if (!string.Equals(_layout, "Model", StringComparison.OrdinalIgnoreCase)) LayoutManager.Current.CurrentLayout = "Model";
            using (ViewTableRecord view = doc.Editor.GetCurrentView()) _twist = view.ViewTwist;
        }

        /// <summary>Turns the model view to a viewport's twist (radians).</summary>
        public void Turn(double twist)
        {
            Editor ed = _doc.Editor;
            using (ViewTableRecord view = ed.GetCurrentView())
            {
                if (Math.Abs(view.ViewTwist - twist) < 1e-9) return;
                view.ViewTwist = twist;
                ed.SetCurrentView(view);
            }
            _turned = true;
            ed.Regen();
        }

        public void Dispose()
        {
            try
            {
                if (_turned)
                    using (ViewTableRecord view = _doc.Editor.GetCurrentView())
                    {
                        view.ViewTwist = _twist;
                        _doc.Editor.SetCurrentView(view);
                    }
                if (!string.Equals(_layout, LayoutManager.Current.CurrentLayout, StringComparison.OrdinalIgnoreCase)) LayoutManager.Current.CurrentLayout = _layout;
            }
            catch (System.Exception ex) { C3DProLogger.Warn("Labels: the view could not be put back as it was", ex); }
        }
    }
}
