using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Parse.Abstractions.Infrastructure;
using Parse.Abstractions.Infrastructure.Execution;
using Parse.Abstractions.Platform.LiveQueries;
using Parse.Infrastructure;
using Parse.Infrastructure.Execution;
using Parse.Infrastructure.Utilities;
using Parse.Platform.LiveQueries;

namespace Parse.Tests;

/// <summary>
/// A <see cref="ParseLiveQueryController"/> wired to the real message parser and real subscriptions, with a
/// scripted server behind a mocked <see cref="IWebSocketClient"/>. It lets tests drive the controller through its
/// public API and feed it raw JSON messages the way the WebSocket client would.
/// </summary>
internal sealed class LiveQueryServerHarness
{
    public const string ClientId = "client-1";

    public Mock<IWebSocketClient> WebSocket { get; } = new();

    public ParseLiveQueryController Controller { get; }

    public ParseClient Client { get; }

    /// <summary>Every message the controller sent to the server, parsed.</summary>
    public List<IDictionary<string, object>> Sent { get; } = [];

    /// <summary>Set to false to make the scripted server stop answering subscribe requests.</summary>
    public bool AnswerSubscribe { get; set; } = true;

    /// <summary>Set to false to make the scripted server stop answering unsubscribe requests.</summary>
    public bool AnswerUnsubscribe { get; set; } = true;

    /// <summary>Set to false to make the scripted server stop answering the connect request.</summary>
    public bool AnswerConnect { get; set; } = true;

    public LiveQueryServerHarness(TimeSpan? timeout = null, bool provideServices = true)
    {
        // Only used to obtain a decoder and services for the parser; the client the tests use is created below.
        ParseClient bootstrap = new(new ServerConnectionData { Test = true }, new LiveQueryServerConnectionData { Test = true });
        bootstrap.Publicize();

        Mock<ILiveQueryServerConnectionData> connectionData = new();
        connectionData.Setup(d => d.ServerURI).Returns("wss://localhost/parse");
        Mock<IServiceHub> services = new();
        services.Setup(h => h.LiveQueryServerConnectionData).Returns(connectionData.Object);

        Mock<IParseLiveQueryMessageBuilder> builder = new();
        builder.Setup(b => b.BuildConnectMessage()).ReturnsAsync(new Dictionary<string, object> { ["op"] = "connect" });
        builder
            .Setup(b => b.BuildSubscribeMessage(It.IsAny<int>(), It.IsAny<ParseLiveQuery<ParseObject>>()))
            .ReturnsAsync((int id, ParseLiveQuery<ParseObject> _) => new Dictionary<string, object> { ["op"] = "subscribe", ["requestId"] = id });
        builder
            .Setup(b => b.BuildUpdateSubscriptionMessage(It.IsAny<int>(), It.IsAny<ParseLiveQuery<ParseObject>>()))
            .ReturnsAsync((int id, ParseLiveQuery<ParseObject> _) => new Dictionary<string, object> { ["op"] = "update", ["requestId"] = id });
        builder
            .Setup(b => b.BuildUnsubscribeMessage(It.IsAny<int>()))
            .Returns((int id) => new Dictionary<string, object> { ["op"] = "unsubscribe", ["requestId"] = id });

        Controller = new ParseLiveQueryController(
            timeout ?? TimeSpan.FromSeconds(2),
            WebSocket.Object,
            new ParseLiveQueryMessageParser(bootstrap.Services.Decoder, bootstrap.Services),
            builder.Object,
            provideServices ? services.Object : null);

        // Subscriptions call back into their hub's controller (CancelAsync, UpdateAsync), so the hub must hand out
        // the scripted controller rather than a default one.
        Client = new ParseClient(
            new ServerConnectionData { Test = true },
            new LiveQueryServerConnectionData { Test = true },
            new MutableServiceHub { LiveQueryController = Controller });
        Client.Publicize();

        WebSocket.Setup(w => w.OpenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        WebSocket.Setup(w => w.CloseAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        WebSocket
            .Setup(w => w.SendAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string json, CancellationToken _) =>
            {
                IDictionary<string, object> message = (IDictionary<string, object>) JsonUtilities.Parse(json);
                Sent.Add(message);
                Answer(message);
                return Task.CompletedTask;
            });
    }

    public ParseLiveQuery<ParseObject> NewQuery(string className = "Corgi") => new(Client.Services, className, new Dictionary<string, object>());

    public async Task<IParseLiveQuerySubscription> ConnectAndSubscribeAsync(string className = "Corgi")
    {
        await Controller.ConnectAsync();
        return await Controller.SubscribeAsync(NewQuery(className));
    }

    /// <summary>Delivers a raw JSON message to the controller as if it had arrived over the socket.</summary>
    public void Receive(string json) => WebSocket.Raise(w => w.MessageReceived += null, this, new MessageReceivedEventArgs(json));

    public void Receive(IDictionary<string, object> message) => Receive(JsonUtilities.Encode(message));

    /// <summary>Builds an event message ("create", "update", ...) for the given subscription.</summary>
    public static Dictionary<string, object> Event(string op, int requestId, string objectId = "obj1", string className = "Corgi", string key = "value", string originalKey = null)
    {
        Dictionary<string, object> message = new()
        {
            ["op"] = op,
            ["clientId"] = ClientId,
            ["requestId"] = requestId,
            ["object"] = ObjectData(objectId, className, key)
        };

        if (originalKey is not null)
            message["original"] = ObjectData(objectId, className, originalKey);

        return message;
    }

    private static Dictionary<string, object> ObjectData(string objectId, string className, string key) => new()
    {
        ["objectId"] = objectId,
        ["className"] = className,
        ["createdAt"] = "2024-01-01T00:00:00.000Z",
        ["updatedAt"] = "2024-01-01T00:00:00.000Z",
        ["key"] = key
    };

    private void Answer(IDictionary<string, object> message)
    {
        string op = (string) message["op"];
        switch (op)
        {
            case "connect" when AnswerConnect:
                Receive(new Dictionary<string, object> { ["op"] = "connected", ["clientId"] = ClientId });
                break;
            case "subscribe" or "update" when AnswerSubscribe:
                Receive(new Dictionary<string, object> { ["op"] = "subscribed", ["clientId"] = ClientId, ["requestId"] = message["requestId"] });
                break;
            case "unsubscribe" when AnswerUnsubscribe:
                Receive(new Dictionary<string, object> { ["op"] = "unsubscribed", ["clientId"] = ClientId, ["requestId"] = message["requestId"] });
                break;
        }
    }
}
