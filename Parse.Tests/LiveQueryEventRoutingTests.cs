using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Parse.Abstractions.Platform.LiveQueries;
using Parse.Platform.LiveQueries;

namespace Parse.Tests;

/// <summary>
/// Drives raw server messages through the real parser and controller and checks that they reach the right
/// subscription event. This is the path that makes a LiveQuery subscription useful.
/// </summary>
[TestClass]
public class LiveQueryEventRoutingTests
{
    private static int RequestIdOf(LiveQueryServerHarness harness) => Convert.ToInt32(harness.Sent[^1]["requestId"]);

    [TestMethod]
    public async Task CreateMessage_RaisesCreate_WithTheDecodedObject()
    {
        LiveQueryServerHarness harness = new();
        IParseLiveQuerySubscription subscription = await harness.ConnectAndSubscribeAsync();
        ParseLiveQueryEventArgs received = null;
        subscription.Create += (_, e) => received = e;

        harness.Receive(LiveQueryServerHarness.Event("create", RequestIdOf(harness), objectId: "abc", key: "fresh"));

        Assert.IsNotNull(received);
        Assert.AreEqual("abc", received.Object.ObjectId);
        Assert.AreEqual("Corgi", received.Object.ClassName);
        Assert.AreEqual("fresh", received.Object["key"]);
    }

    [TestMethod]
    public async Task DeleteMessage_RaisesDelete()
    {
        LiveQueryServerHarness harness = new();
        IParseLiveQuerySubscription subscription = await harness.ConnectAndSubscribeAsync();
        ParseLiveQueryEventArgs received = null;
        subscription.Delete += (_, e) => received = e;

        harness.Receive(LiveQueryServerHarness.Event("delete", RequestIdOf(harness), objectId: "gone"));

        Assert.IsNotNull(received);
        Assert.AreEqual("gone", received.Object.ObjectId);
    }

    [TestMethod]
    [DataRow("update")]
    [DataRow("enter")]
    [DataRow("leave")]
    public async Task DualMessage_RaisesTheMatchingEvent_WithCurrentAndOriginalObjects(string op)
    {
        LiveQueryServerHarness harness = new();
        IParseLiveQuerySubscription subscription = await harness.ConnectAndSubscribeAsync();
        Dictionary<string, ParseLiveQueryDualEventArgs> received = [];
        subscription.Update += (_, e) => received["update"] = e;
        subscription.Enter += (_, e) => received["enter"] = e;
        subscription.Leave += (_, e) => received["leave"] = e;

        harness.Receive(LiveQueryServerHarness.Event(op, RequestIdOf(harness), key: "after", originalKey: "before"));

        Assert.AreEqual(1, received.Count, "Only the event matching the message should be raised.");
        ParseLiveQueryDualEventArgs args = received[op];
        Assert.AreEqual("after", args.Object["key"]);
        Assert.AreEqual("before", args.Original["key"]);
    }

    [TestMethod]
    public async Task Events_AreDeliveredOnlyToTheSubscriptionTheyBelongTo()
    {
        LiveQueryServerHarness harness = new();
        await harness.Controller.ConnectAsync();
        IParseLiveQuerySubscription first = await harness.Controller.SubscribeAsync(harness.NewQuery("Corgi"));
        int firstId = RequestIdOf(harness);
        IParseLiveQuerySubscription second = await harness.Controller.SubscribeAsync(harness.NewQuery("Husky"));
        int secondId = RequestIdOf(harness);
        Assert.AreNotEqual(firstId, secondId);

        int firstCalls = 0, secondCalls = 0;
        first.Create += (_, _) => firstCalls++;
        second.Create += (_, _) => secondCalls++;

        harness.Receive(LiveQueryServerHarness.Event("create", secondId, className: "Husky"));

        Assert.AreEqual(0, firstCalls);
        Assert.AreEqual(1, secondCalls);
    }

    [TestMethod]
    public async Task EventForAnUnknownSubscription_IsIgnored()
    {
        LiveQueryServerHarness harness = new();
        IParseLiveQuerySubscription subscription = await harness.ConnectAndSubscribeAsync();
        int calls = 0;
        subscription.Create += (_, _) => calls++;
        bool errorRaised = false;
        harness.Controller.Error += (_, _) => errorRaised = true;

        harness.Receive(LiveQueryServerHarness.Event("create", requestId: 999));

        Assert.AreEqual(0, calls);
        Assert.IsFalse(errorRaised);
    }

    [TestMethod]
    public async Task EventAfterUnsubscribe_IsNotDelivered()
    {
        LiveQueryServerHarness harness = new();
        IParseLiveQuerySubscription subscription = await harness.ConnectAndSubscribeAsync();
        int requestId = RequestIdOf(harness);
        int calls = 0;
        subscription.Create += (_, _) => calls++;

        await subscription.CancelAsync();
        harness.Receive(LiveQueryServerHarness.Event("create", requestId));

        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    [DataRow("update")]
    [DataRow("enter")]
    [DataRow("leave")]
    public async Task DualMessageWithoutOriginal_IsReportedAsParseError_AndNotDelivered(string op)
    {
        LiveQueryServerHarness harness = new();
        IParseLiveQuerySubscription subscription = await harness.ConnectAndSubscribeAsync();
        int calls = 0;
        subscription.Update += (_, _) => calls++;
        subscription.Enter += (_, _) => calls++;
        subscription.Leave += (_, _) => calls++;
        ParseLiveQueryErrorEventArgs error = null;
        harness.Controller.Error += (_, e) => error = e;

        // No "original" in the message: the parser rejects it.
        harness.Receive(LiveQueryServerHarness.Event(op, RequestIdOf(harness)));

        Assert.AreEqual(0, calls);
        Assert.IsNotNull(error);
        Assert.AreEqual(31, error.Code);
    }

    [TestMethod]
    [DataRow("{\"op\":\"create\",\"requestId\":1,\"object\":{\"objectId\":\"a\",\"className\":\"Corgi\"}}", DisplayName = "missing clientId")]
    [DataRow("{\"op\":\"create\",\"clientId\":\"client-1\",\"object\":{\"objectId\":\"a\",\"className\":\"Corgi\"}}", DisplayName = "missing requestId")]
    [DataRow("{\"op\":\"create\",\"clientId\":\"client-1\",\"requestId\":1}", DisplayName = "missing object")]
    public async Task MalformedEvent_IsNotDelivered_AndRaisesAnError(string json)
    {
        LiveQueryServerHarness harness = new();
        IParseLiveQuerySubscription subscription = await harness.ConnectAndSubscribeAsync();
        int calls = 0;
        subscription.Create += (_, _) => calls++;
        ParseLiveQueryErrorEventArgs error = null;
        harness.Controller.Error += (_, e) => error = e;

        harness.Receive(json);

        Assert.AreEqual(0, calls);
        Assert.IsNotNull(error);
    }

    [TestMethod]
    public async Task NonJsonOrNonObjectMessages_AreIgnored()
    {
        LiveQueryServerHarness harness = new();
        await harness.ConnectAndSubscribeAsync();
        ParseLiveQueryErrorEventArgs error = null;
        harness.Controller.Error += (_, e) => error = e;

        harness.Receive("[1,2,3]");
        harness.Receive("{\"noOp\":true}");
        harness.Receive("{\"op\":\"somethingNew\"}");

        Assert.IsNull(error);
    }
}
