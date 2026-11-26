using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Parse.Abstractions.Platform.LiveQueries;
using Parse.Platform.LiveQueries;

namespace Parse.Tests;

/// <summary>
/// Subscribe, update and unsubscribe round trips against a scripted server, including the failure modes.
/// </summary>
[TestClass]
public class LiveQueryControllerOperationTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(200);

    [TestMethod]
    public async Task SubscribeAsync_AssignsIncreasingRequestIds()
    {
        LiveQueryServerHarness harness = new();
        await harness.Controller.ConnectAsync();

        await harness.Controller.SubscribeAsync(harness.NewQuery());
        await harness.Controller.SubscribeAsync(harness.NewQuery());
        await harness.Controller.SubscribeAsync(harness.NewQuery());

        long[] ids = harness.Sent.Where(m => (string) m["op"] == "subscribe").Select(m => Convert.ToInt64(m["requestId"])).ToArray();
        CollectionAssert.AreEqual(new long[] { 1, 2, 3 }, ids);
    }

    [TestMethod]
    public async Task CancelAsync_SendsUnsubscribe_WaitsForTheAck_AndStopsEvents()
    {
        LiveQueryServerHarness harness = new();
        IParseLiveQuerySubscription subscription = await harness.ConnectAndSubscribeAsync();
        int requestId = Convert.ToInt32(harness.Sent[^1]["requestId"]);
        int events = 0;
        subscription.Create += (_, _) => events++;

        await subscription.CancelAsync();

        Assert.AreEqual("unsubscribe", harness.Sent[^1]["op"]);
        Assert.AreEqual(requestId, Convert.ToInt32(harness.Sent[^1]["requestId"]));
        harness.Receive(LiveQueryServerHarness.Event("create", requestId));
        Assert.AreEqual(0, events);
    }

    [TestMethod]
    public async Task CancelAsync_TimesOut_WhenTheServerDoesNotAcknowledge()
    {
        LiveQueryServerHarness harness = new(ShortTimeout);
        IParseLiveQuerySubscription subscription = await harness.ConnectAndSubscribeAsync();
        harness.AnswerUnsubscribe = false;

        await Assert.ThrowsAsync<TimeoutException>(() => subscription.CancelAsync());
    }

    [TestMethod]
    public async Task UpdateAsync_SendsUpdateForTheSameRequestId_AndKeepsDeliveringEvents()
    {
        LiveQueryServerHarness harness = new();
        IParseLiveQuerySubscription subscription = await harness.ConnectAndSubscribeAsync();
        int requestId = Convert.ToInt32(harness.Sent[^1]["requestId"]);
        int events = 0;
        subscription.Create += (_, _) => events++;

        await subscription.UpdateAsync(harness.NewQuery());

        Assert.AreEqual("update", harness.Sent[^1]["op"]);
        Assert.AreEqual(requestId, Convert.ToInt32(harness.Sent[^1]["requestId"]));
        harness.Receive(LiveQueryServerHarness.Event("create", requestId));
        Assert.AreEqual(1, events);
    }

    [TestMethod]
    public async Task UpdateAsync_TimesOut_WhenTheServerDoesNotAcknowledge()
    {
        LiveQueryServerHarness harness = new(ShortTimeout);
        IParseLiveQuerySubscription subscription = await harness.ConnectAndSubscribeAsync();
        harness.AnswerSubscribe = false;

        await Assert.ThrowsAsync<TimeoutException>(() => subscription.UpdateAsync(harness.NewQuery()));
    }

    [TestMethod]
    public async Task UpdateAsync_WhileAnotherUpdateIsPending_IsRejected()
    {
        LiveQueryServerHarness harness = new(TimeSpan.FromSeconds(5));
        IParseLiveQuerySubscription subscription = await harness.ConnectAndSubscribeAsync();
        harness.AnswerSubscribe = false;
        using CancellationTokenSource cts = new();

        Task first = subscription.UpdateAsync(harness.NewQuery(), cts.Token);
        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => subscription.UpdateAsync(harness.NewQuery()));
        StringAssert.Contains(exception.Message, "already pending");

        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => first);
    }

    [TestMethod]
    public async Task SubscribeAsync_TimesOut_AndDoesNotLeaveASubscriptionBehind()
    {
        LiveQueryServerHarness harness = new(ShortTimeout);
        await harness.Controller.ConnectAsync();
        harness.AnswerSubscribe = false;

        await Assert.ThrowsAsync<TimeoutException>(() => harness.Controller.SubscribeAsync(harness.NewQuery()));

        // A late event for the timed-out request must not be routed anywhere or raise errors.
        bool errorRaised = false;
        harness.Controller.Error += (_, _) => errorRaised = true;
        harness.Receive(LiveQueryServerHarness.Event("create", requestId: 1));
        Assert.IsFalse(errorRaised);
    }

    [TestMethod]
    public async Task SubscribeAsync_CallerCancellation_IsNotReportedAsTimeout()
    {
        LiveQueryServerHarness harness = new(TimeSpan.FromSeconds(5));
        await harness.Controller.ConnectAsync();
        harness.AnswerSubscribe = false;
        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<OperationCanceledException>(() => harness.Controller.SubscribeAsync(harness.NewQuery(), cts.Token));
    }
}
