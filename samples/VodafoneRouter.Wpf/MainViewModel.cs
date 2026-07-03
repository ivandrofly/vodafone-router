using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using VodafoneRouter.Models;

namespace VodafoneRouter.Wpf;

public sealed class MainViewModel : ViewModelBase
{
    private readonly IRouterClientFactory _clientFactory;
    private VodafoneRouterClient? _client;

    private string _routerUrl = Environment.GetEnvironmentVariable("VODAFONE_ROUTER_URL") ?? "http://192.168.1.1/";
    private string _username = Environment.GetEnvironmentVariable("VODAFONE_ROUTER_USER") ?? "vodafone";
    private string _password = Environment.GetEnvironmentVariable("VODAFONE_ROUTER_PASS") ?? "";
    private bool _isConnected;
    private bool _isBusy;
    private string _statusMessage = "Not connected.";
    private RouterOverview? _overview;
    private bool _showActiveOnly = true;
    private string _deviceCountText = "";

    public MainViewModel(IRouterClientFactory clientFactory)
    {
        _clientFactory = clientFactory;

        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => !IsConnected);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, () => IsConnected);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => IsConnected);
        RebootCommand = new AsyncRelayCommand(RebootAsync, () => IsConnected);

        DevicesView = CollectionViewSource.GetDefaultView(Devices);
        DevicesView.Filter = o =>
        {
            if (!ShowActiveOnly)
            {
                return true;
            }

            return ((ConnectedDevice)o).IsActive;
        };
    }

    public string RouterUrl
    {
        get
        {
            return _routerUrl;
        }
        set
        {
            SetField(ref _routerUrl, value);
        }
    }

    public string Username
    {
        get
        {
            return _username;
        }
        set
        {
            SetField(ref _username, value);
        }
    }

    public string Password
    {
        get
        {
            return _password;
        }
        set
        {
            SetField(ref _password, value);
        }
    }

    public bool IsConnected
    {
        get
        {
            return _isConnected;
        }
        private set
        {
            if (SetField(ref _isConnected, value))
            {
                OnPropertyChanged(nameof(IsNotConnected));
                RaiseCommandStates();
            }
        }
    }

    public bool IsNotConnected
    {
        get
        {
            return !IsConnected;
        }
    }

    public bool IsBusy
    {
        get
        {
            return _isBusy;
        }
        private set
        {
            SetField(ref _isBusy, value);
        }
    }

    public string StatusMessage
    {
        get
        {
            return _statusMessage;
        }
        private set
        {
            SetField(ref _statusMessage, value);
        }
    }

    public RouterOverview? Overview
    {
        get
        {
            return _overview;
        }
        private set
        {
            SetField(ref _overview, value);
        }
    }

    public bool ShowActiveOnly
    {
        get
        {
            return _showActiveOnly;
        }
        set
        {
            if (SetField(ref _showActiveOnly, value))
            {
                DevicesView.Refresh();
                UpdateDeviceCounts();
            }
        }
    }

    public string DeviceCountText
    {
        get
        {
            return _deviceCountText;
        }
        private set
        {
            SetField(ref _deviceCountText, value);
        }
    }

    public ObservableCollection<WanConnection> Wans { get; } = new();
    public ObservableCollection<WlanConfig> Wlans { get; } = new();
    public ObservableCollection<ConnectedDevice> Devices { get; } = new();
    public ICollectionView DevicesView { get; }

    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand DisconnectCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand RebootCommand { get; }

    private async Task ConnectAsync()
    {
        if (string.IsNullOrWhiteSpace(Password))
        {
            StatusMessage = "Enter the router password.";
            return;
        }

        IsBusy = true;
        StatusMessage = $"Logging in to {RouterUrl}...";
        try
        {
            var client = _clientFactory.Create(RouterUrl);
            await client.LoginAsync(Username, Password);
            _client = client;
            IsConnected = true;
            StatusMessage = "Connected. Loading data...";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Login failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DisconnectAsync()
    {
        if (_client is not null)
        {
            try
            {
                await _client.DisposeAsync();
            }
            catch
            {
                // Logout failures don't matter; the session cookie just expires.
            }

            _client = null;
        }

        IsConnected = false;
        Overview = null;
        Wans.Clear();
        Wlans.Clear();
        Devices.Clear();
        DeviceCountText = "";
        StatusMessage = "Disconnected.";
    }

    private async Task RefreshAsync()
    {
        if (_client is null)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "Refreshing...";
        try
        {
            Overview = await _client.GetOverviewAsync();

            Wans.Clear();
            foreach (var wan in await _client.GetWanStatusAsync())
            {
                Wans.Add(wan);
            }

            Wlans.Clear();
            foreach (var wlan in await _client.GetWlanConfigAsync())
            {
                Wlans.Add(wlan);
            }

            Devices.Clear();
            foreach (var device in await _client.GetConnectedDevicesAsync())
            {
                Devices.Add(device);
            }

            UpdateDeviceCounts();
            StatusMessage = $"Refreshed at {DateTime.Now:HH:mm:ss}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Refresh failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RebootAsync()
    {
        if (_client is null)
        {
            return;
        }

        var answer = MessageBox.Show(
            "Reboot the router now? It will be unreachable for 1–2 minutes.",
            "Confirm reboot",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "Sending reboot command...";
        try
        {
            await _client.RebootAsync();
            StatusMessage = "Reboot accepted. The router is restarting.";
            await DisconnectAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Reboot failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void UpdateDeviceCounts()
    {
        var active = Devices.Count(d => d.IsActive);
        DeviceCountText = $"{active} active / {Devices.Count} known";
    }

    private void RaiseCommandStates()
    {
        ConnectCommand.RaiseCanExecuteChanged();
        DisconnectCommand.RaiseCanExecuteChanged();
        RefreshCommand.RaiseCanExecuteChanged();
        RebootCommand.RaiseCanExecuteChanged();
    }

    public async Task ShutdownAsync()
    {
        if (_client is not null)
        {
            try
            {
                await _client.DisposeAsync();
            }
            catch
            {
                // Best-effort logout on app shutdown.
            }

            _client = null;
        }
    }
}
