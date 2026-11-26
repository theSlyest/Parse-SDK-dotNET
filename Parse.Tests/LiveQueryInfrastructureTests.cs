using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Parse.Abstractions.Infrastructure;
using Parse.Abstractions.Infrastructure.Execution;
using Parse.Abstractions.Platform.LiveQueries;
using Parse.Infrastructure;
using Parse.Infrastructure.Execution;
using Parse.Platform.LiveQueries;

namespace Parse.Tests;

/// <summary>
/// Smaller pieces of the LiveQuery feature: configuration defaults, service-hub plumbing and the query entry point.
/// </summary>
[TestClass]
public class LiveQueryInfrastructureTests
{
    [TestMethod]
    public void ConnectionData_HasSensibleDefaults()
    {
        LiveQueryServerConnectionData data = new();

        Assert.AreEqual(TimeSpan.FromSeconds(5), LiveQueryServerConnectionData.DefaultTimeout);
        Assert.AreEqual(LiveQueryServerConnectionData.DefaultTimeout, data.Timeout);
        Assert.AreEqual(ILiveQueryServerConnectionData.DefaultBufferSize, data.MessageBufferSize);
        Assert.IsNull(data.ServerURI);
        Assert.IsNull(data.ApplicationID);
        Assert.IsNull(data.Key);
    }

    [TestMethod]
    public void ConnectionData_Timeout_AndBufferSize_AreConfigurable_AndReachTheController()
    {
        LiveQueryServerConnectionData data = new()
        {
            ApplicationID = "appId",
            ServerURI = "wss://parse.example.com/",
            Key = "key",
            Timeout = TimeSpan.FromSeconds(42),
            MessageBufferSize = 123
        };

        ParseClient client = new(new ServerConnectionData { ApplicationID = "appId", ServerURI = "https://parse.example.com/", Key = "key" }, data);

        Assert.AreEqual(TimeSpan.FromSeconds(42), client.LiveQueryServerConnectionData.Timeout);
        Assert.AreEqual(123, client.LiveQueryServerConnectionData.MessageBufferSize);
        Assert.IsNotNull(client.LiveQueryController);
    }

    [TestMethod]
    public void LateInitializedHub_CreatesLiveQueryServices_WhenConfigured_AndReusesThem()
    {
        LateInitializedMutableServiceHub hub = new()
        {
            ServerConnectionData = new ServerConnectionData { ApplicationID = "appId", ServerURI = "https://parse.example.com/", Key = "key" },
            LiveQueryServerConnectionData = new LiveQueryServerConnectionData { ApplicationID = "appId", ServerURI = "wss://parse.example.com/", Key = "key" }
        };

        Assert.IsNotNull(hub.LiveQueryController);
        Assert.IsNotNull(hub.WebSocketClient);
        Assert.IsNotNull(hub.LiveQueryMessageParser);
        Assert.IsNotNull(hub.LiveQueryMessageBuilder);
        Assert.AreSame(hub.LiveQueryController, hub.LiveQueryController);
        Assert.AreSame(hub.WebSocketClient, hub.WebSocketClient);
    }

    [TestMethod]
    public void LateInitializedHub_HasNoController_WithoutLiveQueryConfiguration()
    {
        LateInitializedMutableServiceHub hub = new()
        {
            ServerConnectionData = new ServerConnectionData { ApplicationID = "appId", ServerURI = "https://parse.example.com/", Key = "key" }
        };

        Assert.IsNull(hub.LiveQueryController);
        Assert.IsNull(hub.WebSocketClient);
    }

    private sealed class WrappingHub(IServiceHub inner) : CustomServiceHub
    {
        public override IServiceHub Services { get; internal set; } = inner;
    }

    [TestMethod]
    public void CustomServiceHub_DelegatesLiveQueryServicesToTheWrappedHub()
    {
        Mock<IServiceHub> inner = new();
        IParseLiveQueryController controller = new Mock<IParseLiveQueryController>().Object;
        IWebSocketClient webSocket = new Mock<IWebSocketClient>().Object;
        IParseLiveQueryMessageParser parser = new Mock<IParseLiveQueryMessageParser>().Object;
        IParseLiveQueryMessageBuilder builder = new Mock<IParseLiveQueryMessageBuilder>().Object;
        ILiveQueryServerConnectionData data = new Mock<ILiveQueryServerConnectionData>().Object;
        inner.Setup(h => h.LiveQueryController).Returns(controller);
        inner.Setup(h => h.WebSocketClient).Returns(webSocket);
        inner.Setup(h => h.LiveQueryMessageParser).Returns(parser);
        inner.Setup(h => h.LiveQueryMessageBuilder).Returns(builder);
        inner.Setup(h => h.LiveQueryServerConnectionData).Returns(data);

        WrappingHub hub = new(inner.Object);

        Assert.AreSame(controller, hub.LiveQueryController);
        Assert.AreSame(webSocket, hub.WebSocketClient);
        Assert.AreSame(parser, hub.LiveQueryMessageParser);
        Assert.AreSame(builder, hub.LiveQueryMessageBuilder);
        Assert.AreSame(data, hub.LiveQueryServerConnectionData);
    }

    [TestMethod]
    public async Task ParseLiveQuery_SubscribeAsync_DelegatesToTheController()
    {
        Mock<IParseLiveQueryController> controller = new();
        IParseLiveQuerySubscription subscription = new Mock<IParseLiveQuerySubscription>().Object;
        controller
            .Setup(c => c.SubscribeAsync(It.IsAny<ParseLiveQuery<ParseObject>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(subscription);
        Mock<IServiceHub> hub = new();
        hub.Setup(h => h.LiveQueryController).Returns(controller.Object);
        ParseLiveQuery<ParseObject> query = new(hub.Object, "Corgi", new Dictionary<string, object>());
        using CancellationTokenSource cts = new();

        IParseLiveQuerySubscription result = await query.SubscribeAsync(cts.Token);

        Assert.AreSame(subscription, result);
        controller.Verify(c => c.SubscribeAsync(query, cts.Token), Times.Once);
    }

    [TestMethod]
    public void ParseLiveQuery_RejectsInvalidArguments()
    {
        Mock<IServiceHub> hub = new();

        Assert.ThrowsExactly<ArgumentNullException>(() => new ParseLiveQuery<ParseObject>(null, "Corgi", new Dictionary<string, object>()));
        Assert.ThrowsExactly<ArgumentNullException>(() => new ParseLiveQuery<ParseObject>(hub.Object, "Corgi", null));
        Assert.ThrowsExactly<ArgumentNullException>(() => new ParseLiveQuery<ParseObject>(hub.Object, null, new Dictionary<string, object>()));
        Assert.ThrowsExactly<ArgumentException>(() => new ParseLiveQuery<ParseObject>(hub.Object, "  ", new Dictionary<string, object>()));
    }
}
