using System.Windows.Data;
using System.Windows.Markup;

namespace GitWork.App;

[MarkupExtensionReturnType(typeof(Binding))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension(string text)
    {
        this.Text = text;
    }

    [ConstructorArgument("text")]
    public string Text { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = L10n.CreateBinding(this.Text);

        return binding.ProvideValue(serviceProvider);
    }
}
