using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
namespace SecureVault;
public partial class App : Application
{
    private const string SingleInstanceMutexName = "Bastion.HazzaUxdev.SingleInstance";
    private const string SingleInstancePipeName = "Bastion.HazzaUxdev.Activate";
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;
    private CancellationTokenSource? _activationPipeCts;

    public static bool StartHiddenRequested { get; private set; }
    public static event Action? ActivationRequested;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out var isFirstInstance);
        _ownsSingleInstanceMutex = isFirstInstance;
        if (!isFirstInstance)
        {
            SignalExistingInstance();
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown(0);
            return;
        }

        StartHiddenRequested = e.Args.Any(arg =>
            string.Equals(arg, "--start-hidden", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "/start-hidden", StringComparison.OrdinalIgnoreCase));

        StartActivationPipe();
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activationPipeCts?.Cancel();
        _activationPipeCts?.Dispose();
        if (_ownsSingleInstanceMutex)
            _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static void SignalExistingInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", SingleInstancePipeName, PipeDirection.Out);
            client.Connect(750);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine("activate");
        }
        catch
        {
            // The first instance may still be starting; the mutex still prevents a duplicate vault session.
        }
    }

    private void StartActivationPipe()
    {
        _activationPipeCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!_activationPipeCts.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        SingleInstancePipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Message,
                        PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(_activationPipeCts.Token);
                    using var reader = new StreamReader(server);
                    await reader.ReadLineAsync();
                    Dispatcher.Invoke(() => ActivationRequested?.Invoke());
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    await Task.Delay(250);
                }
            }
        }, _activationPipeCts.Token);
    }
}
