using System.Windows;
using System.Windows.Media;
using System.Windows.Interop;
using NientumStudio.Services;

namespace NientumStudio
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            var settings = EditorSettings.Current;
            RenderOptions.ProcessRenderMode = settings.GpuAcceleration ? RenderMode.Default : RenderMode.SoftwareOnly;
            ThemeManager.Load();
            base.OnStartup(e);
        }
    }
}