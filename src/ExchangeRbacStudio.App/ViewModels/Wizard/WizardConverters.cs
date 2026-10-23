using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ExchangeRbacStudio.App.ViewModels.Wizard;

public static class WizardConverters
{
    /// <summary>Visible when a search has run (value 0) and returned no results (value 1).</summary>
    public static IMultiValueConverter EmptyAfterSearch { get; } = new EmptyAfterSearchConverter();

    private sealed class EmptyAfterSearchConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
            values is [true, int count] && count == 0 ? Visibility.Visible : Visibility.Collapsed;

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
