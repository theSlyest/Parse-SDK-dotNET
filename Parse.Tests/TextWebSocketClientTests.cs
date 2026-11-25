using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Parse.Infrastructure.Execution;

namespace Parse.Tests;

[TestClass]
public class TextWebSocketClientTests
{
    public TestContext TestContext { get; set; }

    [TestMethod]
    public void Dispose_MultipleCalls_DoesNotThrow()
    {
        TextWebSocketClient client = new TextWebSocketClient(1024);
        client.Dispose();
        client.Dispose();
        // No exception expected
    }

    [TestMethod]
    public async Task SendAsync_ThrowsIfNotOpen()
    {
        TextWebSocketClient client = new TextWebSocketClient(1024);
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(async () => await client.SendAsync("msg", CancellationToken.None));
    }

    [TestMethod]
    public async Task CloseAsync_DoesNothingIfNotConnected()
    {
        TextWebSocketClient client = new TextWebSocketClient(1024);
        await client.CloseAsync();
        // Should not throw
    }

    [TestMethod]
    public void StartListening_DoesNotStartMultipleListeners()
    {
        TextWebSocketClient client = new TextWebSocketClient(1024);
        MethodInfo method = typeof(TextWebSocketClient).GetMethod("StartListening", System.Reflection.BindingFlags.NonPublic | BindingFlags.Instance);
        CancellationTokenSource cts = new CancellationTokenSource();
        method?.Invoke(client, new object[] { cts.Token });
        method?.Invoke(client, new object[] { cts.Token });
        // Should not throw or start multiple listeners
    }
    [TestMethod]
    public void TestConstructor()
    {
        TextWebSocketClient client = new TextWebSocketClient(4096);
        Assert.IsNotNull(client);
    }

    [TestMethod]
    [TestCategory("Integration")]
    [Timeout(10000, CooperativeCancellation = true)]
    public async Task TestSendAndReceive()
    {
        // obtain endpoint from environment/config
        string wsUrl = Environment.GetEnvironmentVariable("TEST_WEBSOCKET_ECHO_URL")?.Trim();
        if (String.IsNullOrEmpty(wsUrl))
        {
            // if not provided we cannot run the live integration; mark inconclusive
            // users/CI can set TEST_WEBSOCKET_ECHO_URL to a reachable echo service
            // e.g. wss://echo.example.org or localhost during local development
            Assert.Inconclusive("TEST_WEBSOCKET_ECHO_URL not configured; skipping WebSocket integration test.");
        }

        TextWebSocketClient client = new TextWebSocketClient(32);
        CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        TaskCompletionSource receiveTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await client.OpenAsync(wsUrl, cts.Token);

        string receivedMessage = null;
        EventHandler<MessageReceivedEventArgs> handler = (_, e) =>
        {
            if (e.Message.StartsWith("Request served by"))
            {
                return;
            }
            receivedMessage = e.Message;
            receiveTcs?.TrySetResult();
        };
        client.MessageReceived += handler;

        await client.SendAsync("Hello world, WebSocket listening!", cts.Token);
        cts.CancelAfter(5000);
        await receiveTcs.Task.WaitAsync(cts.Token);
        Assert.AreEqual("Hello world, WebSocket listening!", receivedMessage);

        client.MessageReceived -= handler;
        await client.CloseAsync(cts.Token);
    }

    [TestMethod]
    public async Task SendAsync_ConcurrentCalls_AreAllDelivered()
    {
        const int messageCount = 25;
        System.Net.Sockets.TcpListener portFinder = new(System.Net.IPAddress.Loopback, 0);
        portFinder.Start();
        int port = ((System.Net.IPEndPoint) portFinder.LocalEndpoint).Port;
        portFinder.Stop();

        using System.Net.HttpListener listener = new();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        int received = 0;
        TaskCompletionSource allReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task server = Task.Run(async () =>
        {
            System.Net.HttpListenerContext context = await listener.GetContextAsync();
            System.Net.WebSockets.HttpListenerWebSocketContext wsContext = await context.AcceptWebSocketAsync(null);
            byte[] buffer = new byte[1024];
            while (wsContext.WebSocket.State == System.Net.WebSockets.WebSocketState.Open)
            {
                System.Net.WebSockets.WebSocketReceiveResult result = await wsContext.WebSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                    break;
                if (result.EndOfMessage && Interlocked.Increment(ref received) == messageCount)
                    allReceived.TrySetResult();
            }
        });

        using TextWebSocketClient client = new(1024);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
        await client.OpenAsync($"ws://127.0.0.1:{port}/", cts.Token);

        // ClientWebSocket allows one outstanding send; SendAsync serializes concurrent callers.
        await Task.WhenAll(Enumerable.Range(0, messageCount).Select(n => Task.Run(() => client.SendAsync($"message {n}", cts.Token))));
        await allReceived.Task.WaitAsync(cts.Token);

        Assert.AreEqual(messageCount, received);
        await client.CloseAsync(cts.Token);
        listener.Stop();
    }
}
