using System.IO.Ports;
using System.Text;
using System.Threading.Channels;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

/// <summary>One explicitly selected port. It never discovers, reconnects, or opens another port.</summary>
public sealed class DialSerialTransport : IAsyncDisposable
{
    private readonly string portName;
    private readonly CancellationTokenSource stop = new();
    private readonly Channel<string> input = Channel.CreateBounded<string>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Channel<string> output = Channel.CreateBounded<string>(new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.DropOldest });
    private Task? loop;
    public bool IsOpen { get; private set; }
    public bool Finished => loop?.IsCompleted == true;
    public string? Error { get; private set; }
    public DialSerialTransport(string port, bool demo)
    {
        if (demo) throw new InvalidOperationException("デモではダイヤルのCOMポートを開けません");
        if (!DeviceDialSettingsStore.ValidPort(port)) throw new ArgumentException("USBポートを選択してください");
        portName = port;
    }
    public static string[] Ports() => SerialPort.GetPortNames().Order(StringComparer.OrdinalIgnoreCase).ToArray();
    public void Start() { ObjectDisposedException.ThrowIf(stop.IsCancellationRequested, this); loop ??= Task.Run(Run); }
    public bool TryRead(out string frame) => input.Reader.TryRead(out frame!);
    public void Send(string frame) { if (!stop.IsCancellationRequested && Encoding.UTF8.GetByteCount(frame) + 1 <= 4096) output.Writer.TryWrite(frame); }
    private void Run()
    {
        try
        {
            using var port = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One)
            { Handshake = Handshake.None, DtrEnable = false, RtsEnable = false, ReadTimeout = 10, WriteTimeout = 500,
                Encoding = new UTF8Encoding(false), NewLine = "\n" };
            port.Open(); IsOpen = true;
            port.WriteLine("{\"v\":1,\"type\":\"identify\"}");
            var buffer = new byte[1024]; var framer = new JsonLineFramer(4096);
            while (!stop.IsCancellationRequested)
            {
                var budget = 16;
                while (budget-- > 0 && output.Reader.TryRead(out var frame)) port.WriteLine(frame);
                try { var count = port.Read(buffer, 0, buffer.Length); foreach (var frame in framer.Feed(buffer, count)) input.Writer.TryWrite(frame); }
                catch (TimeoutException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
        { Error = e.Message; }
        finally { IsOpen = false; input.Writer.TryComplete(); output.Writer.TryComplete(); }
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); if (loop is not null) await loop.ConfigureAwait(false); stop.Dispose();
    }
}
