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
            AppLog.InitSession();
            DispatcherUnhandledException += (_, ex) =>
            {
                AppLog.Error("DispatcherUnhandledException", ex.Exception);
            };
            System.AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            {
                AppLog.Error("UnhandledException", ex.ExceptionObject as System.Exception);
            };
            var settings = EditorSettings.Current;
            RenderOptions.ProcessRenderMode = settings.GpuAcceleration ? RenderMode.Default : RenderMode.SoftwareOnly;
            ThemeManager.Load();
            base.OnStartup(e);
        }
    }
}