using System.ComponentModel;
using System.Windows;
using System.Windows.Markup;
using VMCrate.Core;

namespace VMCrate.Desktop;

internal sealed class Labels(TextCatalog catalog) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => catalog[key];
    internal Locale Current => catalog.Current;

    internal void Change(string id)
    {
        catalog.Change(id);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }

    internal string Format(string key, params object[] args) => catalog.Format(key, args);

    internal void Apply(Window window)
    {
        window.FlowDirection = Current.RightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        window.Language = XmlLanguage.GetLanguage(Current.CultureTag);
    }

    internal bool Confirm(Window owner, string key, params object[] args) =>
        MessageBox.Show(owner, Format(key, args), "VMCrate", MessageBoxButton.YesNo,
            MessageBoxImage.Warning, MessageBoxResult.No, MessageOptions) == MessageBoxResult.Yes;

    internal string ErrorText(Exception error) => this[error is LabException problem ? problem.Key : "error.operation"];

    internal void ShowError(Window? owner, Exception error)
    {
        var detail = error is LabException problem ? problem.Detail : error.Message;
        var message = ErrorText(error);
        if (!string.IsNullOrWhiteSpace(detail))
            message += Environment.NewLine + Environment.NewLine + detail[..Math.Min(detail.Length, 1200)];

        if (owner is null)
            MessageBox.Show(message, this["status.error"], MessageBoxButton.OK,
                MessageBoxImage.Warning, MessageBoxResult.OK, MessageOptions);
        else
            MessageBox.Show(owner, message, this["status.error"], MessageBoxButton.OK,
                MessageBoxImage.Warning, MessageBoxResult.OK, MessageOptions);
    }

    internal MessageBoxOptions MessageOptions => Current.RightToLeft
        ? MessageBoxOptions.RightAlign | MessageBoxOptions.RtlReading
        : MessageBoxOptions.None;
}
