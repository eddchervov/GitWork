using System.Windows;

namespace GitWork.App;

public partial class InformationWindow : Window
{
    public InformationWindow(string title, string text)
    {
        this.InitializeComponent();
        this.Title = title;
        this.DetailsText.Text = text;
        this.SourceInitialized += (_, _) => WindowAppearance.ApplyTheme(this);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(this.DetailsText.Text);
            this.CopyStatus.Text = "";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            this.CopyStatus.Text = L10n.T("Буфер обмена занят. Повторите копирование.");
        }
    }
}
