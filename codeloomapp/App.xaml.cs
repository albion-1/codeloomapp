using System.Windows;
using codeloomapp.Services;

namespace codeloomapp
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            GitExecutableLocator.EnsureOnProcessPath();
            base.OnStartup(e);
        }
    }
}
