using System.Text;
using System.Windows;

namespace WinPulse;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Necesario para leer la salida de cmd/sc/powercfg en la página de códigos OEM (850 en español).
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Write("Error inesperado: " + args.Exception);
            MessageBox.Show(args.Exception.Message, "WinRaptor", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        base.OnStartup(e);
    }
}
