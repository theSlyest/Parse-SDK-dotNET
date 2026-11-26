using System;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Parse.Abstractions.Infrastructure;
using Parse.Abstractions.Infrastructure.Execution;
using Parse.Abstractions.Platform.LiveQueries;
using Parse.Platform.LiveQueries;

namespace Parse.Tests;

/// <summary>
/// Connection lifecycle of the LiveQuery controller: guards, timeouts, cancellation and WebSocket error reporting.
/// </summary>
[TestClass]
public class LiveQueryControllerConnectionTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(200);

    [TestMethod]
    public async Task SubscribeAsync_WhenNotConnected_Throws_AndSendsNothing()
    {
        LiveQueryServerHarness harness = new();

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Controller.SubscribeAsync(harness.NewQuery()));

        Assert.AreEqual(0, harness.Sent.Count);
    }

    [TestMethod]
    public async Task ConnectAsync_WithoutServices_ThrowsAndStaysClosed()
    {
        LiveQueryServerHarness harness = new(provideServices: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Controller.ConnectAsync());

        Assert.AreEqual(ParseLiveQueryController.ParseLiveQueryState.Closed, harness.Controller.State);
        harness.WebSocket.Verify(w => w.OpenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task ConnectAsync_WithoutLiveQueryConfiguration_ThrowsAndStaysClosed()
    {
        Mock<IWebSocketClient> webSocket = new();
        Mock<IServiceHub> services = new();
        services.Setup(h => h.LiveQueryServerConnectionData).Returns((ILiveQueryServerConnectionData) null);
        ParseLiveQueryController controller = new(TimeSpan.FromSeconds(1), webSocket.Object, new Mock<IParseLiveQueryMessageParser>().Object, new Mock<IParseLiveQueryMessageBuilder>().Object, services.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.ConnectAsync());

        Assert.AreEqual(ParseLiveQueryController.ParseLiveQueryState.Closed, controller.State);
        webSocket.Verify(w => w.OpenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task ConnectAsync_OpensTheConfiguredServerUri_AndSendsConnect()
    {
        LiveQueryServerHarness harness = new();

        await harness.Controller.ConnectAsync();

        Assert.AreEqual(ParseLiveQueryController.ParseLiveQueryState.Connected, harness.Controller.State);
        harness.WebSocket.Verify(w => w.OpenAsync("wss://localhost/parse", It.IsAny<CancellationToken>()), Times.Once);
        Assert.AreEqual("connect", harness.Sent[0]["op"]);
    }

    [TestMethod]
    public async Task ConnectAsync_WhenAlreadyConnected_DoesNotOpenAgain()
    {
        LiveQueryServerHarness harness = new();
        await harness.Controller.ConnectAsync();

        await harness.Controller.ConnectAsync();

        harness.WebSocket.Verify(w => w.OpenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.AreEqual(1, harness.Sent.Count);
    }

    [TestMethod]
    public async Task ConnectAsync_TimesOut_WhenServerNeverAnswers_AndCleansUp()
    {
        LiveQueryServerHarness harness = new(ShortTimeout);
        harness.AnswerConnect = false;

        await Assert.ThrowsAsync<TimeoutException>(() => harness.Controller.ConnectAsync());

        Assert.AreEqual(ParseLiveQueryController.ParseLiveQueryState.Closed, harness.Controller.State);
        harness.WebSocket.Verify(w => w.CloseAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [TestMethod]
    public async Task ConnectAsync_CallerCancellation_IsNotReportedAsTimeout()
    {
        LiveQueryServerHarness harness = new(TimeSpan.FromSeconds(5));
        harness.AnswerConnect = false;
        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<OperationCanceledException>(() => harness.Controller.ConnectAsync(cts.Token));

        Assert.AreEqual(ParseLiveQueryController.ParseLiveQueryState.Closed, harness.Controller.State);
    }

    [TestMethod]
    public async Task ConnectAsync_WhileConnecting_JoinsTheAttemptInProgress()
    {
        LiveQueryServerHarness harness = new(TimeSpan.FromSeconds(5));
        harness.AnswerConnect = false;

        Task first = harness.Controller.ConnectAsync();
        Task second = harness.Controller.ConnectAsync();
        Assert.IsFalse(first.IsCompleted);
        Assert.IsFalse(second.IsCompleted);
        Assert.AreEqual(ParseLiveQueryController.ParseLiveQueryState.Connecting, harness.Controller.State);

        harness.Receive(new System.Collections.Generic.Dictionary<string, object> { ["op"] = "connected", ["clientId"] = LiveQueryServerHarness.ClientId });
        await Task.WhenAll(first, second);

        Assert.AreEqual(ParseLiveQueryController.ParseLiveQueryState.Connected, harness.Controller.State);
        harness.WebSocket.Verify(w => w.OpenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task CanReconnect_AfterClose()
    {
        LiveQueryServerHarness harness = new();
        await harness.Controller.ConnectAsync();
        await harness.Controller.CloseAsync();
        Assert.AreEqual(ParseLiveQueryController.ParseLiveQueryState.Closed, harness.Controller.State);

        await harness.Controller.ConnectAsync();

        Assert.AreEqual(ParseLiveQueryController.ParseLiveQueryState.Connected, harness.Controller.State);
        harness.WebSocket.Verify(w => w.OpenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [TestMethod]
    public async Task WebSocketException_IsReportedAsNonReconnectableError()
    {
        LiveQueryServerHarness harness = new();
        await harness.Controller.ConnectAsync();
        ParseLiveQueryErrorEventArgs error = null;
        harness.Controller.Error += (_, e) => error = e;
        WebSocketException exception = new("socket failed");

        harness.WebSocket.Raise(w => w.WebsocketError += null, this, new ErrorEventArgs(exception));

        Assert.IsNotNull(error);
        Assert.IsFalse(error.Reconnect);
        Assert.AreEqual("socket failed", error.Error);
        Assert.AreSame(exception, error.LocalException);
    }

    [TestMethod]
    public async Task UnexpectedWebSocketClientError_IsReportedWithCodeMinusOne()
    {
        LiveQueryServerHarness harness = new();
        await harness.Controller.ConnectAsync();
        ParseLiveQueryErrorEventArgs error = null;
        harness.Controller.Error += (_, e) => error = e;
        InvalidOperationException exception = new("unexpected");

        harness.WebSocket.Raise(w => w.UnknownError += null, this, new ErrorEventArgs(exception));

        Assert.IsNotNull(error);
        Assert.AreEqual(-1, error.Code);
        Assert.IsFalse(error.Reconnect);
        Assert.AreSame(exception, error.LocalException);
    }

    [TestMethod]
    public async Task WebSocketClientErrors_AreIgnored_AfterClose()
    {
        LiveQueryServerHarness harness = new();
        await harness.Controller.ConnectAsync();
        await harness.Controller.CloseAsync();
        bool raised = false;
        harness.Controller.Error += (_, _) => raised = true;

        harness.WebSocket.Raise(w => w.UnknownError += null, this, new ErrorEventArgs(new InvalidOperationException("late")));

        Assert.IsFalse(raised);
    }
}
