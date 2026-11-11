using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexusMods.Sdk.Settings;
using NexusMods.ProxyConsole;
using NexusMods.SingleProcess.Exceptions;

namespace NexusMods.SingleProcess;

/// <summary>
/// A long-running service that listens for incoming connections from clients and executes them as if they ran
/// on as CLI command.
/// </summary>
public sealed class CliServer : IHostedService, IDisposable
{
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private CancellationToken Token => _cancellationTokenSource.Token;

    private bool _started;

    private TcpListener? _tcpListener;
    private readonly ConcurrentDictionary<Guid, Task> _runningClients = [];

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<CliServer> _logger;
    private readonly CommandLineConfigurator _configurator;
    private readonly SyncFile _syncFile;
    private byte[] _authKey = [];

    /// <summary>
    /// Constructor.
    /// </summary>
    public CliServer(
        ILogger<CliServer> logger,
        CommandLineConfigurator configurator,
        ISettingsManager settingsManager,
        SyncFile syncFile,
        IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _configurator = configurator;
        _syncFile = syncFile;

        settingsManager.Get<CliSettings>();
    }
    
    /// <summary>
    /// Starts the CLI server, listening for incoming connections.
    /// This method needs to be called explicitly to start the server.
    /// </summary>
    public async Task StartCliServerAsync()
    {
        if (!_started)
        {
            _started = true;
            await StartTcpListenerAsync();
        }
    }

    private Task StartTcpListenerAsync()
    {
        _tcpListener = new TcpListener(IPAddress.Loopback, 0);
        _tcpListener.Start();
        Task.Run(async () => await StartListeningAsync(), _cancellationTokenSource.Token);
        var port = ((IPEndPoint)_tcpListener.LocalEndpoint).Port;

        if (!_syncFile.TrySetMain(port))
        {
            _logger.LogError("Failed to set main process in shared array, another process is likely running");
            throw new SingleProcessLockException();
        }

        // Only write the secret once we know we're the main process, so we never overwrite another instance's key.
        _authKey = _syncFile.CreateAuthKey();

        _logger.LogInformation("Started TCP listener on port {Port}", port);
        return Task.CompletedTask;
    }

    private async Task StartListeningAsync()
    {
        while (_started && !_cancellationTokenSource.IsCancellationRequested)
        {
            try
            {
                var found = await _tcpListener!.AcceptTcpClientAsync(Token);
                found.NoDelay = true; // Disable Nagle's algorithm to reduce delay.

                var id = Guid.NewGuid();
                var task = Task.Run(() => HandleClientAsync(id, found), Token);
                _ = _runningClients.GetOrAdd(id, task);

                _logger.LogInformation("Accepted TCP connection from {RemoteEndPoint}", ((IPEndPoint)found.Client.RemoteEndPoint!).Port);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("TCP listener was cancelled, stopping");
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Got an exception while accepting a client connection");
            }
        }
    }

    /// <summary>
    /// Handle a client connection
    /// </summary>
    private async Task HandleClientAsync(Guid id, TcpClient client)
    {
        try
        {
            var stream = client.GetStream();

            // Afterburn: every client must first send the session secret. Anything else is dropped.
            var received = new byte[SyncFile.AuthKeyLength];
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                try { await stream.ReadExactlyAsync(received, timeout.Token); }
                catch (Exception) { _logger.LogWarning("Rejected a local connection that didn't authenticate"); return; }
            }

            if (_authKey.Length != SyncFile.AuthKeyLength || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(received, _authKey))
            {
                _logger.LogWarning("Rejected a local connection with the wrong key");
                return;
            }

            var (arguments, renderer) = await ProxiedRenderer.Create(_serviceProvider, stream);
            await _configurator.RunAsync(arguments ?? [], renderer, Token);
        }
        finally
        {
            client.Dispose();
            _runningClients.Remove(id, out _);
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_started) return; 

        // Ditch this value and don't wait on it because it otherwise blocks the shutdown even when *no-one* is 
        // waiting on the token
        _ = _cancellationTokenSource.CancelAsync();

        _tcpListener?.Stop();
        await Task.WhenAll(_runningClients.Values.ToArray());
        _started = false;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_started) return;
        _started = false;
        _cancellationTokenSource.Dispose();
        _tcpListener?.Dispose();
    }
}
