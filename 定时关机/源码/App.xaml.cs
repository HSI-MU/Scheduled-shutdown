using System.Threading;
using System.Windows;

namespace OnTimeShutdown;

public partial class App : Application
{
    private const string MutexName = @"Local\OnTimeShutdown.SingleInstance";
    private const string ShowEventName = @"Local\OnTimeShutdown.ShowWindow";

    private Mutex? _instanceMutex;
    private bool _ownsMutex;
    private EventWaitHandle? _showWindowEvent;
    private Thread? _showWindowListener;
    private volatile bool _isListening;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(initiallyOwned: true, MutexName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            _instanceMutex.Dispose();
            _instanceMutex = null;
            SignalExistingInstance();
            Shutdown();
            return;
        }

        _ownsMutex = true;

        _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _isListening = true;
        _showWindowListener = new Thread(ListenForShowRequests)
        {
            IsBackground = true,
            Name = "OnTimeShutdown.ShowWindowListener"
        };
        _showWindowListener.Start();

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _isListening = false;
        _showWindowEvent?.Set();
        _showWindowEvent?.Dispose();
        if (_ownsMutex)
        {
            _instanceMutex?.ReleaseMutex();
            _ownsMutex = false;
        }

        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void ListenForShowRequests()
    {
        while (_isListening)
        {
            try
            {
                if (_showWindowEvent?.WaitOne() != true)
                {
                    return;
                }
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (!_isListening)
            {
                return;
            }

            Dispatcher.BeginInvoke(() =>
            {
                if (MainWindow is MainWindow window)
                {
                    window.RestoreFromTray();
                }
            });
        }
    }

    private static void SignalExistingInstance()
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(ShowEventName);
            handle.Set();
        }
        catch
        {
            MessageBox.Show(
                "准点关机已经在运行，请在任务栏或系统托盘中打开它。",
                "准点关机",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }
}
