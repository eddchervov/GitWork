using System.Text.RegularExpressions;
using System.Windows;
using GitWork.Core;

namespace GitWork.App;

public partial class PreparationVariableWindow : Window
{
    public PreparationVariableWindow(PreparationVariable? variable = null)
    {
        this.InitializeComponent();
        this.SourceInitialized += (_, _) => WindowAppearance.ApplyTheme(this);
        if (variable is not null)
        {
            this.DialogTitle.Text = L10n.T("Изменить переменную окружения");
            this.NameBox.Text = variable.Name;
            this.SecretBox.IsChecked = variable.IsSecret;
            if (variable.IsSecret)
            {
                this.SecretValueBox.Password = variable.Value;
            }
            else
            {
                this.ValueBox.Text = variable.Value;
            }
        }

        this.UpdateValueVisibility();
    }

    public PreparationVariable? Result { get; private set; }

    private void Secret_Changed(object sender, RoutedEventArgs e)
    {
        if (this.ValueBox is null || this.SecretValueBox is null)
        {
            return;
        }

        var value = this.SecretBox.IsChecked == true ? this.ValueBox.Text : this.SecretValueBox.Password;
        if (this.SecretBox.IsChecked == true)
        {
            this.SecretValueBox.Password = value;
        }
        else
        {
            this.ValueBox.Text = value;
        }

        this.UpdateValueVisibility();
    }

    private void UpdateValueVisibility()
    {
        var isSecret = this.SecretBox.IsChecked == true;
        this.ValueBox.Visibility = isSecret ? Visibility.Collapsed : Visibility.Visible;
        this.SecretValueBox.Visibility = isSecret ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = this.NameBox.Text.Trim();
        var value = this.SecretBox.IsChecked == true ? this.SecretValueBox.Password : this.ValueBox.Text;
        if (!Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant))
        {
            this.ValidationText.Text = L10n.T("Имя должно начинаться с буквы или подчёркивания и содержать только латинские буквы, цифры и подчёркивания.");

            return;
        }

        var isSecret = this.SecretBox.IsChecked == true;
        if (value.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            this.ValidationText.Text = L10n.T("Значение переменной должно занимать одну строку без нулевых символов.");

            return;
        }

        try
        {
            this.Result = new PreparationVariable
            {
                Name = name,
                Value = isSecret ? PreparationSecrets.Protect(value) : value,
                IsSecret = isSecret
            };
        }
        catch (System.Security.Cryptography.CryptographicException exception)
        {
            this.ValidationText.Text = exception.Message;

            return;
        }

        this.DialogResult = true;
    }
}
