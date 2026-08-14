using System.Reflection;
using System.Windows;

namespace CadenceCisLibraryManager;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = GetVersionText();
    }

    private static string GetVersionText()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version is null ? "未知" : version.ToString();
    }
}
