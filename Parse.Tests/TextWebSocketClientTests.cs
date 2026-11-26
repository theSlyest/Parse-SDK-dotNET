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

    [TestMethod]
    public async Task StartListening_StartsListenerForReplacedSocket_WhileOldListenerIsStillRunning()
    {
        System.Net.Sockets.TcpListener portFinder = new(System.Net.IPAddress.Loopback, 0);
        portFinder.Start();
        int port = ((System.Net.IPEndPoint) portFinder.LocalEndpoint).Port;
        portFinder.Stop();

        using System.Net.HttpListener listener = new();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        // The server accepts the connection and keeps it open, so the first listener stays running.
        Task<System.Net.HttpListenerContext> accept = listener.GetContextAsync();
        _ = accept.ContinueWith(t => t.Result.AcceptWebSocketAsync(null), TaskContinuationOptions.OnlyOnRanToCompletion);

        using TextWebSocketClient client = new(1024);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
        await client.OpenAsync($"ws://127.0.0.1:{port}/", cts.Token);

        BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        FieldInfo socketField = typeof(TextWebSocketClient).GetField("webSocket", flags);
        FieldInfo listeningSocketField = typeof(TextWebSocketClient).GetField("listeningSocket", flags);
        Task firstListener = (Task) typeof(TextWebSocketClient).GetField("listeningTask", flags).GetValue(client);
        Assert.IsFalse(firstListener.IsCompleted, "The first listener should still be running.");

        // Simulate a reconnect: the socket field now points at a new socket while the old listener winds down.
        System.Net.WebSockets.ClientWebSocket replacement = new();
        socketField.SetValue(client, replacement);
        typeof(TextWebSocketClient).GetMethod("StartListening", flags).Invoke(client, new object[] { cts.Token });

        Assert.AreSame(replacement, listeningSocketField.GetValue(client), "A listener must be started for the new socket.");
        listener.Abort();
    }

    [TestMethod]
    public async Task Closed_IsRaised_WhenServerClosesTheConnection_ButNotOnLocalClose()
    {
        System.Net.Sockets.TcpListener portFinder = new(System.Net.IPAddress.Loopback, 0);
        portFinder.Start();
        int port = ((System.Net.IPEndPoint) portFinder.LocalEndpoint).Port;
        portFinder.Stop();

        using System.Net.HttpListener listener = new();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        TaskCompletionSource<System.Net.WebSockets.WebSocket> serverSocket = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            System.Net.HttpListenerContext context = await listener.GetContextAsync();
            serverSocket.SetResult((await context.AcceptWebSocketAsync(null)).WebSocket);
        });

        using TextWebSocketClient client = new(1024);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
        int closedCount = 0;
        TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Closed += (_, _) => { Interlocked.Increment(ref closedCount); closed.TrySetResult(); };

        await client.OpenAsync($"ws://127.0.0.1:{port}/", cts.Token);
        System.Net.WebSockets.WebSocket server = await serverSocket.Task.WaitAsync(cts.Token);

        await server.CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "bye", cts.Token);
        await closed.Task.WaitAsync(cts.Token);
        Assert.AreEqual(1, closedCount);

        // A close requested by the client itself is not reported as a loss of connection.
        await client.CloseAsync(cts.Token);
        Assert.AreEqual(1, closedCount);
        listener.Stop();
    }

    private static async Task<(System.Net.HttpListener Listener, int Port, TaskCompletionSource<System.Net.WebSockets.WebSocket> Server)> StartServerAsync()
    {
        System.Net.Sockets.TcpListener portFinder = new(System.Net.IPAddress.Loopback, 0);
        portFinder.Start();
        int port = ((System.Net.IPEndPoint) portFinder.LocalEndpoint).Port;
        portFinder.Stop();

        System.Net.HttpListener listener = new();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        TaskCompletionSource<System.Net.WebSockets.WebSocket> server = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            System.Net.HttpListenerContext context = await listener.GetContextAsync();
            server.SetResult((await context.AcceptWebSocketAsync(null)).WebSocket);
        });
        await Task.CompletedTask;
        return (listener, port, server);
    }

    [TestMethod]
    public async Task ServerInitiatedClose_CompletesTheClosingHandshake()
    {
        (System.Net.HttpListener listener, int port, TaskCompletionSource<System.Net.WebSockets.WebSocket> serverTask) = await StartServerAsync();
        using (listener)
        {
            using TextWebSocketClient client = new(1024);
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
            await client.OpenAsync($"ws://127.0.0.1:{port}/", cts.Token);
            System.Net.WebSockets.WebSocket server = await serverTask.Task.WaitAsync(cts.Token);

            // The server waits for the client's close frame, so this only completes if the client answers.
            await server.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "bye", cts.Token);

            Assert.AreEqual(System.Net.WebSockets.WebSocketState.Closed, server.State);
            listener.Stop();
        }
    }

    [TestMethod]
    public async Task OversizedFragmentedMessage_IsRejected_AndReportedAsError()
    {
        (System.Net.HttpListener listener, int port, TaskCompletionSource<System.Net.WebSockets.WebSocket> serverTask) = await StartServerAsync();
        using (listener)
        {
            using TextWebSocketClient client = new(1024);
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(20));
            TaskCompletionSource<Exception> error = new(TaskCreationOptions.RunContinuationsAsynchronously);
            bool messageDelivered = false;
            client.UnknownError += (_, e) => error.TrySetResult(e.GetException());
            client.MessageReceived += (_, _) => messageDelivered = true;
            await client.OpenAsync($"ws://127.0.0.1:{port}/", cts.Token);
            System.Net.WebSockets.WebSocket server = await serverTask.Task.WaitAsync(cts.Token);

            byte[] chunk = new byte[64 * 1024];
            Array.Fill(chunk, (byte) 'a');
            int chunks = TextWebSocketClient.MaxMessageSize / chunk.Length + 2;
            try
            {
                for (int n = 0; n < chunks; n++)
                {
                    await server.SendAsync(chunk, System.Net.WebSockets.WebSocketMessageType.Text, n == chunks - 1, cts.Token);
                }
            }
            catch (System.Net.WebSockets.WebSocketException)
            {
                // The client closes the connection once the limit is exceeded.
            }

            Exception exception = await error.Task.WaitAsync(cts.Token);
            Assert.IsInstanceOfType<InvalidOperationException>(exception);
            Assert.IsFalse(messageDelivered);
            listener.Stop();
        }
    }

    [TestMethod]
    public async Task OpenAsync_WhileAlreadyOpen_DoesNotConnectAgain()
    {
        (System.Net.HttpListener listener, int port, TaskCompletionSource<System.Net.WebSockets.WebSocket> serverTask) = await StartServerAsync();
        using (listener)
        {
            using TextWebSocketClient client = new(1024);
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
            await client.OpenAsync($"ws://127.0.0.1:{port}/", cts.Token);
            System.Net.WebSockets.WebSocket server = await serverTask.Task.WaitAsync(cts.Token);

            // A second open is a no-op: no new connection (the listener only accepts one) and the socket keeps working.
            await client.OpenAsync($"ws://127.0.0.1:{port}/", cts.Token);
            await client.SendAsync("still connected", cts.Token);

            byte[] buffer = new byte[64];
            System.Net.WebSockets.WebSocketReceiveResult result = await server.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
            Assert.AreEqual("still connected", System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count));
            listener.Stop();
        }
    }

    [TestMethod]
    public async Task OpenAsync_CanConnectAgain_AfterClose()
    {
        System.Net.Sockets.TcpListener portFinder = new(System.Net.IPAddress.Loopback, 0);
        portFinder.Start();
        int port = ((System.Net.IPEndPoint) portFinder.LocalEndpoint).Port;
        portFinder.Stop();

        using System.Net.HttpListener listener = new();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        int accepted = 0;
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                try
                {
                    System.Net.HttpListenerContext context = await listener.GetContextAsync();
                    await context.AcceptWebSocketAsync(null);
                    Interlocked.Increment(ref accepted);
                }
                catch (Exception)
                {
                    return;
                }
            }
        });

        using TextWebSocketClient client = new(1024);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
        await client.OpenAsync($"ws://127.0.0.1:{port}/", cts.Token);
        await client.CloseAsync(cts.Token);
        await client.OpenAsync($"ws://127.0.0.1:{port}/", cts.Token);
        await client.SendAsync("after reconnect", cts.Token);

        Assert.AreEqual(2, Volatile.Read(ref accepted));
        await client.CloseAsync(cts.Token);
        listener.Stop();
    }

    [TestMethod]
    public async Task OpenAsync_WhenConnectFails_Throws_AndALaterAttemptCanSucceed()
    {
        System.Net.Sockets.TcpListener portFinder = new(System.Net.IPAddress.Loopback, 0);
        portFinder.Start();
        int closedPort = ((System.Net.IPEndPoint) portFinder.LocalEndpoint).Port;
        portFinder.Stop();

        using TextWebSocketClient client = new(1024);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<System.Net.WebSockets.WebSocketException>(() => client.OpenAsync($"ws://127.0.0.1:{closedPort}/", cts.Token));

        // The failed attempt must not leave a half-open socket behind.
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.SendAsync("nothing is open", cts.Token));

        (System.Net.HttpListener listener, int port, TaskCompletionSource<System.Net.WebSockets.WebSocket> serverTask) = await StartServerAsync();
        using (listener)
        {
            await client.OpenAsync($"ws://127.0.0.1:{port}/", cts.Token);
            await serverTask.Task.WaitAsync(cts.Token);
            await client.SendAsync("connected now", cts.Token);
            listener.Stop();
        }
    }

    [TestMethod]
    public async Task SendAsync_AfterClose_Throws()
    {
        (System.Net.HttpListener listener, int port, TaskCompletionSource<System.Net.WebSockets.WebSocket> serverTask) = await StartServerAsync();
        using (listener)
        {
            using TextWebSocketClient client = new(1024);
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
            await client.OpenAsync($"ws://127.0.0.1:{port}/", cts.Token);
            await serverTask.Task.WaitAsync(cts.Token);
            await client.CloseAsync(cts.Token);

            await Assert.ThrowsAsync<ArgumentNullException>(() => client.SendAsync("too late", cts.Token));
            listener.Stop();
        }
    }
}
