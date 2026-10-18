using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Principal;
using System.Windows;

namespace TNovCloud.Client.Services;

internal sealed class ShellCommandServer : IDisposable
{
    private const int MaximumPayloadBytes = 1024 * 1024;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _listener;
    private int _disposed;

    public ShellCommandServer()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value
                  ?? throw new InvalidOperationException("Не удалось определить SID пользователя.");
        PipeName = $"TNovCloud.Client.Commands.{sid}";
        _listener = Task.Run(ListenAsync);
    }

    public string PipeName { get; }

    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token);

                var prefix = new byte[sizeof(uint)];
                await ReadExactlyAsync(pipe, prefix, _stop.Token);
                var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(prefix));
                if (length is <= 0 or > MaximumPayloadBytes)
                    throw new InvalidDataException("Некорректная длина shell-команды.");

                var payload = new byte[length];
                await ReadExactlyAsync(pipe, payload, _stop.Token);
                var request = ShellCommandProtocol.Decode(payload);
                var argument = ShellCommandProtocol.ToArgument(request.Command);

                foreach (var path in request.Paths)
                {
                    var operation = Application.Current.Dispatcher.InvokeAsync(
                        () => CommandHandler.RunAsync([argument, path]));
                    await operation.Task.Unwrap();
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.WriteLine(exception);
            }
        }
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        while (!buffer.IsEmpty)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                throw new EndOfStreamException("Shell extension closed the command pipe.");
            buffer = buffer[read..];
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _stop.Cancel();
        try
        {
            _listener.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // Cancellation is expected during application shutdown.
        }
        _stop.Dispose();
    }
}
