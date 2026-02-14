using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Win32;
using WowProxy.Infrastructure;
using WowProxy.Core.Abstractions;
using WowProxy.Core.Abstractions.Models;
using WowProxy.Domain;

namespace WowProxy.App.ViewModels;

public class SettingsViewModel : INotifyPropertyChanged
{
    private readonly MainViewModel _mainViewModel;
    private string? _singBoxPath;
    private string _mixedPortText;
    private bool _enableClashApi;
    private string _clashApiPortText;
    private string? _clashApiSecret;
    private string _logLevel;
    private bool _enableDirectCn;

    public SettingsViewModel(MainViewModel mainViewModel, AppSettings settings)
    {
        _mainViewModel = mainViewModel;
        _singBoxPath = settings.SingBoxPath;
        _mixedPortText = settings.MixedPort.ToString();
        _enableClashApi = settings.EnableClashApi;
        _clashApiPortText = settings.ClashApiPort.ToString();
        _clashApiSecret = settings.ClashApiSecret;
        _logLevel = string.IsNullOrWhiteSpace(settings.LogLevel) ? "info" : settings.LogLevel;
        _enableDirectCn = settings.EnableDirectCn;

        BrowseSingBoxCommand = new RelayCommand(_ => BrowseSingBox());
    }

    public RelayCommand BrowseSingBoxCommand { get; }

    public string? SingBoxPath
    {
        get => _singBoxPath;
        set
        {
            if (_singBoxPath == value) return;
            _singBoxPath = value;
            OnPropertyChanged();
            _mainViewModel.NotifySettingsChanged();
        }
    }

    public string MixedPortText
    {
        get => _mixedPortText;
        set
        {
            if (_mixedPortText == value) return;
            _mixedPortText = value;
            OnPropertyChanged();
            _mainViewModel.NotifySettingsChanged();
        }
    }

    public bool EnableClashApi
    {
        get => _enableClashApi;
        set
        {
            if (_enableClashApi == value) return;
            _enableClashApi = value;
            OnPropertyChanged();
            _mainViewModel.NotifySettingsChanged();
        }
    }

    public string ClashApiPortText
    {
        get => _clashApiPortText;
        set
        {
            if (_clashApiPortText == value) return;
            _clashApiPortText = value;
            OnPropertyChanged();
            _mainViewModel.NotifySettingsChanged();
        }
    }

    public string? ClashApiSecret
    {
        get => _clashApiSecret;
        set
        {
            if (_clashApiSecret == value) return;
            _clashApiSecret = value;
            OnPropertyChanged();
            _mainViewModel.NotifySettingsChanged();
        }
    }

    public string LogLevel
    {
        get => _logLevel;
        set
        {
            if (_logLevel == value) return;
            _logLevel = value;
            OnPropertyChanged();
            _mainViewModel.NotifySettingsChanged();
        }
    }

    public bool EnableDirectCn
    {
        get => _enableDirectCn;
        set
        {
            if (_enableDirectCn == value) return;
            _enableDirectCn = value;
            OnPropertyChanged();
            _mainViewModel.NotifySettingsChanged();
        }
    }

    private void BrowseSingBox()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 sing-box.exe",
            Filter = "sing-box.exe|sing-box.exe|可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
        };

        if (dialog.ShowDialog() == true)
        {
            SingBoxPath = dialog.FileName;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
