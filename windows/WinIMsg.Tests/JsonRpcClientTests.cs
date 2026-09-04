using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using WinIMsg.Core.Rpc;

namespace WinIMsg.Tests;

public sealed class JsonRpcClientTests
{
    [Fact]
    public async Task InvokeAsyncCorrelatesResponseWithRequest()
    {
        var reader = new ChannelLineReader();
        var writer = new FakeRpcWriter(reader);
        await using var client = new JsonRpcClient(reader, writer, TimeSpan.FromSeconds(5));

        writer.OnRequest = request =>
        {
            var id = request.RootElement.GetProperty("id").GetInt64();
            reader.Enqueue($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{\"chats\":[{{\"id\":1,\"identifier\":\"+1\",\"guid\":\"iMessage;-;+1\",\"participants\":[\"+1\"]}}]}}}}");
        };

        var result = await client.InvokeAsync<JsonElement>("chats.list", new { limit = 1 });

        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        Assert.Contains("\"method\":\"chats.list\"", writer.Writes[0]);
        Assert.True(result.GetProperty("chats")[0].GetProperty("id").GetInt64() == 1);
    }

    [Fact]
    public async Task InvokeAsyncThrowsRemoteError()
    {
        var reader = new ChannelLineReader();
        var writer = new FakeRpcWriter(reader);
        await using var client = new JsonRpcClient(reader, writer, TimeSpan.FromSeconds(5));
        writer.OnRequest = request =>
        {
            var id = request.RootElement.GetProperty("id").GetInt64();
            reader.Enqueue($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"error\":{{\"code\":-32602,\"message\":\"missing chat_id\"}}}}");
        };

        var ex = await Assert.ThrowsAsync<JsonRpcRemoteException>(() => client.InvokeAsync<JsonElement>("messages.history"));

        Assert.Equal(-32602, ex.Code);
        Assert.Contains("missing chat_id", ex.Message);
    }

    [Fact]
    public async Task NotificationEventParsesWatchMessage()
    {
        var reader = new ChannelLineReader();
        var writer = new FakeRpcWriter(reader);
        await using var client = new JsonRpcClient(reader, writer, TimeSpan.FromSeconds(5));
        JsonRpcNotification? notification = null;
        client.NotificationReceived += (_, value) => notification = value;

        client.Start();
        reader.Enqueue("""{"jsonrpc":"2.0","method":"message","params":{"subscription":1,"message":{"guid":"m1","chat_id":2,"text":"hi","is_from_me":false}}}""");
        await SpinUntilAsync(() => notification is not null);

        Assert.NotNull(notification);
        Assert.True(notification!.TryGetMessage(out var message));
        Assert.Equal("m1", message.Guid);
        Assert.Equal("hi", message.Text);
    }

    [Fact]
    public async Task ReaderDisconnectFailsPendingRequest()
    {
        var reader = new ChannelLineReader();
        var writer = new FakeRpcWriter(reader);
        await using var client = new JsonRpcClient(reader, writer, TimeSpan.FromSeconds(5));
        JsonRpcConnectionClosedEventArgs? closed = null;
        client.ConnectionClosed += (_, args) => closed = args;
        writer.OnRequest = _ => reader.Complete();

        await Assert.ThrowsAsync<EndOfStreamException>(() => client.InvokeAsync<JsonElement>("chats.list"));
        await SpinUntilAsync(() => closed is not null);

        Assert.IsType<EndOfStreamException>(closed!.Exception);
    }

    [Fact]
    public async Task InvokeAsyncAfterReaderDisconnectFailsImmediately()
    {
        var reader = new ChannelLineReader();
        var writer = new FakeRpcWriter(reader);
        await using var client = new JsonRpcClient(reader, writer, TimeSpan.FromSeconds(5));
        JsonRpcConnectionClosedEventArgs? closed = null;
        client.ConnectionClosed += (_, args) => closed = args;

        client.Start();
        reader.Complete();
        await SpinUntilAsync(() => closed is not null);

        var started = DateTime.UtcNow;
        await Assert.ThrowsAsync<EndOfStreamException>(() => client.InvokeAsync<JsonElement>("chats.list"));

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1));
        Assert.Empty(writer.Writes);
    }

    private static async Task SpinUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.True(condition());
    }

    private sealed class ChannelLineReader : TextReader
    {
        private readonly Channel<string?> _lines = Channel.CreateUnbounded<string?>();

        public void Enqueue(string line) => _lines.Writer.TryWrite(line);

        public void Complete() => _lines.Writer.TryComplete();

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await _lines.Reader.ReadAsync(cancellationToken);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }
    }

    private sealed class FakeRpcWriter(ChannelLineReader reader) : TextWriter
    {
        public List<string> Writes { get; } = [];

        public Action<JsonDocument>? OnRequest { get; set; }

        public override Encoding Encoding => Encoding.UTF8;

        public override Task WriteLineAsync(string? value)
        {
            if (value is not null)
            {
                Writes.Add(value);
                using var document = JsonDocument.Parse(value);
                OnRequest?.Invoke(document);
            }

            return Task.CompletedTask;
        }

        protected override void Dispose(bool disposing)
        {
            reader.Complete();
            base.Dispose(disposing);
        }
    }
}
