using System.Windows;
using System.Windows.Controls;

namespace VodafoneRouter.Wpf;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = _viewModel;

        // PasswordBox.Password is not a dependency property, so it can't be
        // bound; seed it from the env-var default the view model picked up.
        PasswordInput.Password = _viewModel.Password;
    }

    private void PasswordInput_PasswordChanged(object sender, RoutedEventArgs e)
    {
        _viewModel.Password = ((PasswordBox)sender).Password;
    }

    protected override async void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        await _viewModel.ShutdownAsync();
    }
}
