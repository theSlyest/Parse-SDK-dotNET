using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Parse.Abstractions.Infrastructure;
using Parse.Abstractions.Infrastructure.Execution;
using Parse.Abstractions.Platform.LiveQueries;
using Parse.Infrastructure.Execution;
using Parse.Platform.LiveQueries;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Reflection;

namespace Parse.Tests;

[TestClass]
public class LiveQueryControllerTests
{
    private readonly Mock<IWebSocketClient> _webSocketClientMock;
    private readonly Mock<IParseLiveQueryMessageParser> _messageParserMock;
    private readonly Mock<IParseLiveQueryMessageBuilder> _messageBuilderMock;
    private readonly TimeSpan _timeout = TimeSpan.FromSeconds(1);

    public LiveQueryControllerTests()
    {
        _webSocketClientMock = new Mock<IWebSocketClient>();
        _messageParserMock = new Mock<IParseLiveQueryMessageParser>();
        _messageBuilderMock = new Mock<IParseLiveQueryMessageBuilder>();
    }

    [TestMethod]
    public void ProcessMessage_UnknownOperation_DoesNotThrow()
    {
        ParseLiveQueryController controller = new ParseLiveQueryController(_timeout, _webSocketClientMock.Object, _messageParserMock.Object, _messageBuilderMock.Object);
        Dictionary<string, object> message = new Dictionary<string, object> { { "op", "unknown" } };
        MethodInfo processMessage = controller.GetType().GetMethod("ProcessMessage", BindingFlags.NonPublic | BindingFlags.Instance);
        processMessage.Invoke(controller, new object[] { message });
        // No exception expected
    }

    [TestMethod]
    public void ProcessMessage_MissingOp_DoesNotThrow()
    {
        ParseLiveQueryController controller = new ParseLiveQueryController(_timeout, _webSocketClientMock.Object, _messageParserMock.Object, _messageBuilderMock.Object);
        Dictionary<string, object> message = new Dictionary<string, object>();
        MethodInfo processMessage = controller.GetType().GetMethod("ProcessMessage", BindingFlags.NonPublic | BindingFlags.Instance);
        processMessage.Invoke(controller, new object[] { message });
        // No exception expected
    }

    [TestMethod]
    public void ProcessConnectionMessage_SetsStateConnected()
    {
        ParseLiveQueryController controller = new ParseLiveQueryController(_timeout, _webSocketClientMock.Object, _messageParserMock.Object, _messageBuilderMock.Object);
        _messageParserMock.Setup(m => m.GetClientId(It.IsAny<IDictionary<string, object>>())).Returns("clientId");
        Dictionary<string, object> message = new Dictionary<string, object> { { "op", "connected" } };
        MethodInfo processConnectionMessage = controller.GetType().GetMethod("ProcessConnectionMessage", BindingFlags.NonPublic | BindingFlags.Instance);
        processConnectionMessage.Invoke(controller, new object[] { message });
        Assert.AreEqual(ParseLiveQueryController.ParseLiveQueryState.Connected, controller.State);
    }

    [TestMethod]
    public void ProcessErrorMessage_RaisesErrorEvent()
    {
        ParseLiveQueryController controller = new ParseLiveQueryController(_timeout, _webSocketClientMock.Object, _messageParserMock.Object, _messageBuilderMock.Object);
        IParseLiveQueryMessageParser.LiveQueryError error = new IParseLiveQueryMessageParser.LiveQueryError { Code = 99, Message = "err", Reconnect = false };
        _messageParserMock.Setup(m => m.GetError(It.IsAny<IDictionary<string, object>>())).Returns(error);
        bool raised = false;
        controller.Error += (s, e) => { raised = true; Assert.AreEqual(99, e.Code); };
        MethodInfo processErrorMessage = controller.GetType().GetMethod("ProcessErrorMessage", BindingFlags.NonPublic | BindingFlags.Instance);
        processErrorMessage.Invoke(controller, new object[] { new Dictionary<string, object>() });
        Assert.IsTrue(raised);
    }

    [TestMethod]
    public void Constructor_InitializesStateToClosed()
    {
        ParseLiveQueryController controller = new ParseLiveQueryController(_timeout, _webSocketClientMock.Object, _messageParserMock.Object, _messageBuilderMock.Object);
        Assert.AreEqual(ParseLiveQueryController.ParseLiveQueryState.Closed, controller.State);
    }

    [TestMethod]
    public async Task ConnectAsync_ThrowsIfDisposed()
    {
        ParseLiveQueryController controller = new ParseLiveQueryController(_timeout, _webSocketClientMock.Object, _messageParserMock.Object, _messageBuilderMock.Object);
        controller.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => controller.ConnectAsync());
    }

    [TestMethod]
    public async Task DisposeAsync_ClosesConnection()
    {
        ParseLiveQueryController controller = new ParseLiveQueryController(_timeout, _webSocketClientMock.Object, _messageParserMock.Object, _messageBuilderMock.Object);
        _webSocketClientMock.Setup(w => w.CloseAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask).Verifiable();
        await controller.DisposeAsync();
        _webSocketClientMock.Verify(w => w.CloseAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public void Dispose_SetsDisposedFlag()
    {
        ParseLiveQueryController controller = new ParseLiveQueryController(_timeout, _webSocketClientMock.Object, _messageParserMock.Object, _messageBuilderMock.Object);
        controller.Dispose();
        Assert.Throws<ObjectDisposedException>(() => controller.ConnectAsync().GetAwaiter().GetResult());
    }

    [TestMethod]
    public void Error_Event_Is_Raised_On_Error_Message()
    {
        ParseLiveQueryController controller = new ParseLiveQueryController(_timeout, _webSocketClientMock.Object, _messageParserMock.Object, _messageBuilderMock.Object);
        IParseLiveQueryMessageParser.LiveQueryError error = new IParseLiveQueryMessageParser.LiveQueryError { Code = 42, Message = "Test error", Reconnect = true };
        _messageParserMock.Setup(m => m.GetError(It.IsAny<IDictionary<string, object>>())).Returns(error);
        bool eventRaised = false;
        controller.Error += (sender, args) =>
        {
            eventRaised = true;
            Assert.AreEqual(42, args.Code);
            Assert.AreEqual("Test error", args.Error);
            Assert.IsTrue(args.Reconnect);
        };
        Dictionary<string, object> message = new Dictionary<string, object> { { "op", "error" } };
        MethodInfo processMessage = controller.GetType().GetMethod("ProcessMessage", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        processMessage.Invoke(controller, new object[] { message });
        Assert.IsTrue(eventRaised);
    }

    [TestMethod]
    public void ValidateClientMessage_ReturnsFalse_WhenClientIdMissing()
    {
        var controller = new ParseLiveQueryController(_timeout, _webSocketClientMock.Object, _messageParserMock.Object, _messageBuilderMock.Object);
        MethodInfo validate = controller.GetType().GetMethod("ValidateClientMessage", BindingFlags.NonPublic | BindingFlags.Instance);

        _messageParserMock.Setup(m => m.GetClientId(It.IsAny<IDictionary<string, object>>())).Returns((string)null);
        _messageParserMock.Setup(m => m.GetRequestId(It.IsAny<IDictionary<string, object>>())).Returns(123);

        var message = new Dictionary<string, object>();
        object[] args = { message, 0 };
        bool result = (bool)validate.Invoke(controller, args);

        Assert.IsFalse(result, "Expected validation to fail when client id is null");
        // requestId should remain default since client id was invalid
        Assert.AreEqual(0, (int)args[1]);
    }

    [TestMethod]
    public void ValidateClientMessage_ReturnsFalse_WhenRequestIdZero()
    {
        var controller = new ParseLiveQueryController(_timeout, _webSocketClientMock.Object, _messageParserMock.Object, _messageBuilderMock.Object);
        MethodInfo validate = controller.GetType().GetMethod("ValidateClientMessage", BindingFlags.NonPublic | BindingFlags.Instance);

        _messageParserMock.Setup(m => m.GetClientId(It.IsAny<IDictionary<string, object>>())).Returns("cid");
        _messageParserMock.Setup(m => m.GetRequestId(It.IsAny<IDictionary<string, object>>())).Returns(0);

        var message = new Dictionary<string, object>();
        object[] args = { message, 0 };
        bool result = (bool)validate.Invoke(controller, args);

        Assert.IsFalse(result, "Expected validation to fail when request id is zero");
        Assert.AreEqual(0, (int)args[1]);
    }

    [TestMethod]
    public void ValidateClientMessage_ReturnsTrue_AndSetsFields_WhenValid()
    {
        var controller = new ParseLiveQueryController(_timeout, _webSocketClientMock.Object, _messageParserMock.Object, _messageBuilderMock.Object);
        MethodInfo validate = controller.GetType().GetMethod("ValidateClientMessage", BindingFlags.NonPublic | BindingFlags.Instance);

        _messageParserMock.Setup(m => m.GetClientId(It.IsAny<IDictionary<string, object>>())).Returns("cid");
        _messageParserMock.Setup(m => m.GetRequestId(It.IsAny<IDictionary<string, object>>())).Returns(999);

        var message = new Dictionary<string, object>();
        object[] args = { message, 0 };
        bool result = (bool)validate.Invoke(controller, args);

        Assert.IsTrue(result, "Expected validation to succeed with valid client id and request id");
        Assert.AreEqual(999, (int)args[1]);

        // verify controller's internal ClientId property was set appropriately
        PropertyInfo clientIdProp = controller.GetType().GetProperty("ClientId", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.AreEqual("cid", clientIdProp.GetValue(controller));
    }

    private ParseLiveQueryController CreateConnectedController()
    {
        ParseLiveQueryController controller = new ParseLiveQueryController(_timeout, _webSocketClientMock.Object, _messageParserMock.Object, _messageBuilderMock.Object);
        typeof(ParseLiveQueryController).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance)
            .SetValue(controller, ParseLiveQueryController.ParseLiveQueryState.Connected);
        _messageBuilderMock
            .Setup(b => b.BuildSubscribeMessage(It.IsAny<int>(), It.IsAny<ParseLiveQuery<ParseObject>>()))
            .ReturnsAsync(new Dictionary<string, object> { ["op"] = "subscribe" });
        return controller;
    }

    private static int RegisteredSubscriptionCount(ParseLiveQueryController controller) =>
        ((System.Collections.ICollection) typeof(ParseLiveQueryController)
            .GetProperty("Subscriptions", BindingFlags.NonPublic | BindingFlags.Instance)
            .GetValue(controller)).Count;

    [TestMethod]
    public async Task SubscribeAsync_RegistersSubscriptionBeforeAckIsReceived()
    {
        ParseLiveQueryController controller = CreateConnectedController();
        _messageParserMock.Setup(m => m.GetClientId(It.IsAny<IDictionary<string, object>>())).Returns("cid");
        _messageParserMock.Setup(m => m.GetRequestId(It.IsAny<IDictionary<string, object>>())).Returns(1);
        MethodInfo processSubscription = typeof(ParseLiveQueryController).GetMethod("ProcessSubscriptionMessage", BindingFlags.NonPublic | BindingFlags.Instance);

        int registeredWhenSent = -1;
        _webSocketClientMock
            .Setup(w => w.SendAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                // Events can follow the ack immediately, so the subscription must already be registered.
                registeredWhenSent = RegisteredSubscriptionCount(controller);
                processSubscription.Invoke(controller, new object[] { new Dictionary<string, object>() });
                return Task.CompletedTask;
            });

        ParseLiveQuery<ParseObject> query = new(new Mock<IServiceHub>().Object, "Corgi", new Dictionary<string, object>());
        IParseLiveQuerySubscription subscription = await controller.SubscribeAsync(query);

        Assert.IsNotNull(subscription);
        Assert.AreEqual(1, registeredWhenSent);
        Assert.AreEqual(1, RegisteredSubscriptionCount(controller));
    }

    [TestMethod]
    public async Task SubscribeAsync_RemovesSubscription_WhenSendFails()
    {
        ParseLiveQueryController controller = CreateConnectedController();
        _webSocketClientMock
            .Setup(w => w.SendAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("closed"));

        ParseLiveQuery<ParseObject> query = new(new Mock<IServiceHub>().Object, "Corgi", new Dictionary<string, object>());

        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.SubscribeAsync(query));
        Assert.AreEqual(0, RegisteredSubscriptionCount(controller));
    }

    [TestMethod]
    public void MessageReceived_HandlerThatThrows_RaisesErrorAndDoesNotPropagate()
    {
        ParseLiveQueryController controller = new ParseLiveQueryController(_timeout, _webSocketClientMock.Object, _messageParserMock.Object, _messageBuilderMock.Object);
        // The parser throwing a non-ArgumentException stands in for a user event handler that throws.
        _messageParserMock.Setup(m => m.GetError(It.IsAny<IDictionary<string, object>>())).Throws(new InvalidOperationException("boom"));

        ParseLiveQueryErrorEventArgs received = null;
        controller.Error += (_, e) => received = e;

        MethodInfo onMessage = typeof(ParseLiveQueryController).GetMethod("WebSocketClientOnMessageReceived", BindingFlags.NonPublic | BindingFlags.Instance);
        onMessage.Invoke(controller, new object[] { null, new MessageReceivedEventArgs("{\"op\":\"error\"}") });

        Assert.IsNotNull(received);
        Assert.AreEqual(-1, received.Code);
        Assert.IsFalse(received.Reconnect);
    }

    [TestMethod]
    public void MessageReceived_ErrorHandlerThatThrows_DoesNotPropagate()
    {
        ParseLiveQueryController controller = new ParseLiveQueryController(_timeout, _webSocketClientMock.Object, _messageParserMock.Object, _messageBuilderMock.Object);
        _messageParserMock.Setup(m => m.GetError(It.IsAny<IDictionary<string, object>>())).Throws(new InvalidOperationException("boom"));
        controller.Error += (_, _) => throw new InvalidOperationException("handler failure");

        MethodInfo onMessage = typeof(ParseLiveQueryController).GetMethod("WebSocketClientOnMessageReceived", BindingFlags.NonPublic | BindingFlags.Instance);
        onMessage.Invoke(controller, new object[] { null, new MessageReceivedEventArgs("{\"op\":\"error\"}") });
    }
}
