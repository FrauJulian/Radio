using System.Windows.Data;
using System.Windows.Markup;
using Radiosender.Services.Localization;

namespace Radiosender.Views;

// {l:Tr Key}: localized text that follows language switches. Keys: letters, digits, underscore.
[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension(string key) : MarkupExtension
{
    public string Key { get; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}
