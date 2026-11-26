using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using Parse.Abstractions.Infrastructure;
using Parse.Infrastructure;

namespace Parse.Tests;

[TestClass]
public class ClientTests
{
    [TestMethod]
    public void TestParseClientConstructor()
    {
        ParseClient client = new("appId", "https://parse.example.com/", "dotnetKey");
        Assert.AreEqual("appId", client.ServerConnectionData.ApplicationID);
        Assert.AreEqual("https://parse.example.com/", client.ServerConnectionData.ServerURI);
        Assert.AreEqual("dotnetKey", client.ServerConnectionData.Key);
    }

    [TestMethod]
    public void TestParseClientLiveQueryConstructor()
    {
        ServerConnectionData serverConnectionData = new()
        {
            ApplicationID = "appId",
            ServerURI = "https://parse.example.com/",
            Key = "dotnetKey"
        };

        LiveQueryServerConnectionData liveQueryServerConnectionData = new()
        {
            ApplicationID = "appId",
            ServerURI = "wss://parse.example.com/",
            Key = "dotnetKey"
        };

        ParseClient client = new(serverConnectionData, liveQueryServerConnectionData);
        Assert.AreEqual("appId", client.ServerConnectionData.ApplicationID);
        Assert.AreEqual("https://parse.example.com/", client.ServerConnectionData.ServerURI);
        Assert.AreEqual("dotnetKey", client.ServerConnectionData.Key);

        Assert.AreEqual("appId", client.LiveQueryServerConnectionData.ApplicationID);
        Assert.AreEqual("wss://parse.example.com/", client.LiveQueryServerConnectionData.ServerURI);
        Assert.AreEqual("dotnetKey", client.LiveQueryServerConnectionData.Key);
    }

    [TestMethod]
    public void TestPublicize()
    {
        ParseClient previous = ParseClient.Instance;
        ParseClient client = new("appId", "https://parse.example.com/", "dotnetKey");
        try {
            client.Publicize();
            Assert.AreSame(client, ParseClient.Instance);
        } finally {
            previous?.Publicize();
        }
    }

    [TestMethod]
    public void TestConstructorWithTestTrue()
    {
        ServerConnectionData data = new()
        {
            ApplicationID = "appId",
            ServerURI = "https://parse.example.com/",
            Key = "key",
            Test = true
        };
        ParseClient client = new(data);
        Assert.AreEqual("https://parse.example.com/", client.ServerConnectionData.ServerURI);
    }

    private static ServerConnectionData ValidServerData() => new() { ApplicationID = "appId", ServerURI = "https://parse.example.com/", Key = "key" };

    private static LiveQueryServerConnectionData ValidLiveQueryData() => new() { ApplicationID = "appId", ServerURI = "wss://parse.example.com/", Key = "key" };

    [TestMethod]
    public void TestLiveQueryConstructorRejectsNullConfigurations()
    {
        ArgumentNullException liveQuery = Assert.ThrowsExactly<ArgumentNullException>(() => new ParseClient(ValidServerData(), (ILiveQueryServerConnectionData) null));
        Assert.AreEqual("liveQueryConfiguration", liveQuery.ParamName);

        ArgumentNullException server = Assert.ThrowsExactly<ArgumentNullException>(() => new ParseClient((IServerConnectionData) null, ValidLiveQueryData()));
        Assert.AreEqual("configuration", server.ParamName);
    }

    [TestMethod]
    public void TestLiveQueryConstructorRejectsOfficialParseUri() =>
        Assert.ThrowsExactly<InvalidOperationException>(() => new ParseClient(ValidServerData(), new LiveQueryServerConnectionData { ApplicationID = "appId", ServerURI = "wss://api.parse.com/1/", Key = "key" }));

    [TestMethod]
    public void TestLiveQueryConstructorRejectsIncompleteConfiguration()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => new ParseClient(ValidServerData(), new LiveQueryServerConnectionData { ApplicationID = "appId", ServerURI = "wss://parse.example.com/" }));
        Assert.ThrowsExactly<InvalidOperationException>(() => new ParseClient(ValidServerData(), new LiveQueryServerConnectionData { ServerURI = "wss://parse.example.com/", Key = "key" }));
        Assert.ThrowsExactly<InvalidOperationException>(() => new ParseClient(ValidServerData(), new LiveQueryServerConnectionData { ApplicationID = "appId", Key = "key" }));
    }

    [TestMethod]
    public void TestLiveQueryConstructorWithTestTrue()
    {
        ParseClient withUri = new(ValidServerData(), new LiveQueryServerConnectionData { ApplicationID = "appId", ServerURI = "wss://parse.example.com/", Key = "key", Test = true });
        Assert.AreEqual("wss://parse.example.com/", withUri.LiveQueryServerConnectionData.ServerURI);

        // Test mode fills in a placeholder URI instead of failing when none is given.
        ParseClient withoutUri = new(new ServerConnectionData { Test = true }, new LiveQueryServerConnectionData { Test = true });
        Assert.AreEqual("wss://api.parse.com/1/", withoutUri.LiveQueryServerConnectionData.ServerURI);
    }

    [TestMethod]
    public void TestLiveQueryConstructorCreatesTheLiveQueryController()
    {
        ParseClient client = new(ValidServerData(), ValidLiveQueryData());
        Assert.IsNotNull(client.LiveQueryController);

        // Without LiveQuery configuration there is no controller to connect with.
        ParseClient withoutLiveQuery = new(ValidServerData());
        Assert.IsNull(withoutLiveQuery.LiveQueryServerConnectionData);
        Assert.IsNull(withoutLiveQuery.LiveQueryController);
    }

    [TestMethod]
    public void TestLiveQueryConstructorKeepsConfigurationWithCustomHub()
    {
        MutableServiceHub custom = new();
        ParseClient client = new(ValidServerData(), ValidLiveQueryData(), custom);

        Assert.AreEqual("wss://parse.example.com/", client.LiveQueryServerConnectionData.ServerURI);
        Assert.AreEqual("https://parse.example.com/", client.ServerConnectionData.ServerURI);
    }

    private sealed class RecordingMutator : IServiceHubMutator
    {
        public bool Valid => true;

        public int Calls { get; private set; }

        public void Mutate(ref IMutableServiceHub target, in IServiceHub composedHub) => Calls++;
    }

    [TestMethod]
    public void TestLiveQueryConstructorAppliesConfiguratorsToAMutableHub()
    {
        RecordingMutator mutator = new();
        MutableServiceHub custom = new();

        ParseClient client = new(ValidServerData(), ValidLiveQueryData(), custom, mutator);

        Assert.AreEqual(1, mutator.Calls);
        // The configuration is copied onto the hub the configurators work on.
        Assert.AreEqual("wss://parse.example.com/", custom.LiveQueryServerConnectionData.ServerURI);
        Assert.AreEqual("wss://parse.example.com/", client.LiveQueryServerConnectionData.ServerURI);
    }

    [TestMethod]
    public void TestConstructorWithoutLiveQueryLeavesAMutableHubsLiveQueryConfigurationAlone()
    {
        LiveQueryServerConnectionData existing = ValidLiveQueryData();
        MutableServiceHub custom = new() { LiveQueryServerConnectionData = existing };

        _ = new ParseClient(ValidServerData(), custom, new RecordingMutator());

        Assert.AreEqual("wss://parse.example.com/", custom.LiveQueryServerConnectionData.ServerURI);
    }
}
