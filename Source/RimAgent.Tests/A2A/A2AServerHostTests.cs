using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using A2A;
using Grpc.Net.Client;
using NUnit.Framework;
using RimAgent.A2A;
using Protos = A2A.Grpc.Protos;

namespace RimAgent.Tests.A2A;

[TestFixture]
[NonParallelizable]
public class A2AServerHostTests
{
    private IAsyncDisposable _server;
    private int _httpPort;
    private int _grpcPort;
    private readonly List<string> _received = [];
    private Func<string, CancellationToken, Task<string>> _behavior;

    [SetUp]
    public async Task StartServer()
    {
        _httpPort = FreePort();
        _grpcPort = FreePort();
        _received.Clear();
        _behavior = (text, _) => Task.FromResult("did: " + text);
        _server = await A2AServerHost.StartAsync(AppContext.BaseDirectory, _httpPort, _grpcPort, "test",
            (text, ct) => { lock (_received) _received.Add(text); return _behavior(text, ct); });
    }

    [TearDown]
    public async Task StopServer()
    {
        await _server.DisposeAsync();
    }

    [Test]
    public async Task JsonRpcSendMessageRunsTheInstructionAndCompletesTheTask()
    {
        using var http = new HttpClient();
        var client = new A2AClient(new Uri("http://localhost:" + _httpPort + A2AServerHost.JsonRpcPath), http);

        SendMessageResponse response = await client.SendMessageAsync(Request("build a wall"), CancellationToken.None);

        Assert.That(_received, Is.EqualTo(new[] { "build a wall" }));
        Assert.That(response.Task, Is.Not.Null);
        Assert.That(response.Task.Status.State, Is.EqualTo(TaskState.Completed));
        Assert.That(response.Task.Status.Message.Parts[0].Text, Is.EqualTo("did: build a wall"));
    }

    [Test]
    public async Task GrpcSendMessageRunsTheInstructionAndCompletesTheTask()
    {
        using GrpcChannel channel = GrpcChannel.ForAddress("http://localhost:" + _grpcPort);
        var client = new Protos.A2AService.A2AServiceClient(channel);
        var request = new Protos.SendMessageRequest { Message = new Protos.Message { MessageId = "m1", Role = Protos.Role.User } };
        request.Message.Parts.Add(new Protos.Part { Text = "how many colonists?" });

        Protos.SendMessageResponse response = await client.SendMessageAsync(request);

        Assert.That(_received, Is.EqualTo(new[] { "how many colonists?" }));
        Assert.That(response.Task.Status.State, Is.EqualTo(Protos.TaskState.Completed));
        Assert.That(response.Task.Status.Message.Parts[0].Text, Is.EqualTo("did: how many colonists?"));

        Protos.Task fetched = await client.GetTaskAsync(new Protos.GetTaskRequest { Id = response.Task.Id });
        Assert.That(fetched.Status.State, Is.EqualTo(Protos.TaskState.Completed));
    }

    [Test]
    public async Task RunnerFailureFailsTheTaskWithItsMessage()
    {
        _behavior = (_, _) => throw new InvalidOperationException("RimAgent is busy.");
        using var http = new HttpClient();
        var client = new A2AClient(new Uri("http://localhost:" + _httpPort + A2AServerHost.JsonRpcPath), http);

        SendMessageResponse response = await client.SendMessageAsync(Request("anything"), CancellationToken.None);

        Assert.That(response.Task.Status.State, Is.EqualTo(TaskState.Failed));
        Assert.That(response.Task.Status.Message.Parts[0].Text, Is.EqualTo("RimAgent is busy."));
    }

    [Test]
    public async Task CancelTaskStopsTheRunningInstruction()
    {
        var started = new TaskCompletionSource();
        var observedCancel = new TaskCompletionSource();
        _behavior = async (_, ct) =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { observedCancel.SetResult(); throw; }
            return "unreachable";
        };
        using var http = new HttpClient();
        var client = new A2AClient(new Uri("http://localhost:" + _httpPort + A2AServerHost.JsonRpcPath), http);
        SendMessageRequest request = Request("dig a long tunnel");
        request.Configuration = new SendMessageConfiguration { ReturnImmediately = true };

        SendMessageResponse response = await client.SendMessageAsync(request, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await client.CancelTaskAsync(new CancelTaskRequest { Id = response.Task.Id }, CancellationToken.None);
        await observedCancel.Task.WaitAsync(TimeSpan.FromSeconds(10));

        AgentTask task = null;
        for (int i = 0; i < 50 && task?.Status.State != TaskState.Canceled; i++)
        {
            task = await client.GetTaskAsync(new GetTaskRequest { Id = response.Task.Id }, CancellationToken.None);
            if (task.Status.State != TaskState.Canceled) await Task.Delay(100);
        }
        Assert.That(task.Status.State, Is.EqualTo(TaskState.Canceled));
    }

    [Test]
    public async Task BlockingClientGivingUpStopsTheInstruction()
    {
        var started = new TaskCompletionSource();
        var observedCancel = new TaskCompletionSource();
        _behavior = async (_, ct) =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { observedCancel.SetResult(); throw; }
            return "unreachable";
        };
        using var http = new HttpClient();
        var client = new A2AClient(new Uri("http://localhost:" + _httpPort + A2AServerHost.JsonRpcPath), http);
        using var giveUp = new CancellationTokenSource();

        Task<SendMessageResponse> blocking = client.SendMessageAsync(Request("long job"), giveUp.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await giveUp.CancelAsync();

        Assert.That(async () => await blocking, Throws.InstanceOf<OperationCanceledException>());
        await observedCancel.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task ForeignHostHeaderIsRejected()
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost:" + _httpPort + "/.well-known/agent-card.json");
        request.Headers.Host = "evil.example";

        using HttpResponseMessage response = await http.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    private static async Task<string> OnlyTaskId(A2AClient client)
    {
        ListTasksResponse list = await client.ListTasksAsync(new ListTasksRequest(), CancellationToken.None);
        Assert.That(list.Tasks, Has.Count.EqualTo(1));
        return list.Tasks[0].Id;
    }

    private static async Task<AgentTask> WaitForTerminal(A2AClient client, string id)
    {
        AgentTask task = null;
        for (int i = 0; i < 100; i++)
        {
            task = await client.GetTaskAsync(new GetTaskRequest { Id = id }, CancellationToken.None);
            if (task.Status.State is TaskState.Completed or TaskState.Canceled or TaskState.Failed) return task;
            await Task.Delay(50);
        }
        return task;
    }

    [Test]
    public async Task GrpcStreamingReportsWorkingThenCompleted()
    {
        using GrpcChannel channel = GrpcChannel.ForAddress("http://localhost:" + _grpcPort);
        var client = new Protos.A2AService.A2AServiceClient(channel);
        var request = new Protos.SendMessageRequest { Message = new Protos.Message { MessageId = "m2", Role = Protos.Role.User } };
        request.Message.Parts.Add(new Protos.Part { Text = "status" });

        var states = new List<Protos.TaskState>();
        using var call = client.SendStreamingMessage(request);
        while (await call.ResponseStream.MoveNext(CancellationToken.None))
        {
            Protos.StreamResponse e = call.ResponseStream.Current;
            if (e.StatusUpdate != null) states.Add(e.StatusUpdate.Status.State);
            if (e.Task != null) states.Add(e.Task.Status.State);
        }

        Assert.That(states, Does.Contain(Protos.TaskState.Working));
        Assert.That(states[^1], Is.EqualTo(Protos.TaskState.Completed));
    }

    [Test]
    public async Task AgentCardAdvertisesJsonRpcAndGrpc()
    {
        using var http = new HttpClient();
        AgentCard card = await new A2ACardResolver(new Uri("http://localhost:" + _httpPort), http).GetAgentCardAsync();

        Assert.That(card.Name, Is.EqualTo("RimAgent"));
        Assert.That(card.SupportedInterfaces.ConvertAll(i => i.ProtocolBinding),
            Is.EquivalentTo(new[] { ProtocolBindingNames.JsonRpc, ProtocolBindingNames.Grpc }));
    }

    private static SendMessageRequest Request(string text) => new()
    {
        Message = new Message { MessageId = Guid.NewGuid().ToString("N"), Role = Role.User, Parts = [Part.FromText(text)] },
    };

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
