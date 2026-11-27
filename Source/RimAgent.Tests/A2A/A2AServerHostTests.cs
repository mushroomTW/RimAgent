using System;
using System.Collections.Generic;
using System.Globalization;
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
            (text, ct) => { lock (_received) _received.Add(text); return _behavior(text, ct); }, Token);
    }

    [TearDown]
    public async Task StopServer()
    {
        await _server.DisposeAsync();
    }

    private const string Token = "fixture-token";

    private static HttpClient AuthorizedHttp()
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);
        return http;
    }

    private static Grpc.Core.Metadata GrpcAuth() => new() { { "Authorization", "Bearer " + Token } };

    private string HttpUrl(string path = "") => string.Create(CultureInfo.InvariantCulture, $"http://localhost:{_httpPort}{path}");

    private string GrpcUrl() => string.Create(CultureInfo.InvariantCulture, $"http://localhost:{_grpcPort}");

    /// <summary>瀏覽器頁面能不經預檢送出 text/plain 的跨站 POST，Host 又是 localhost；不擋的話任何網頁都能指揮殖民地。</summary>
    [TestCase("text/plain", null)]
    [TestCase("application/json", "https://evil.example")]
    [TestCase("text/plain", "https://evil.example")]
    public async Task CrossSiteBrowserRequestsAreRejected(string contentType, string origin)
    {
        using HttpClient http = AuthorizedHttp();
        using var request = new HttpRequestMessage(HttpMethod.Post, HttpUrl("/a2a"));
        request.Content = new StringContent(
            "{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"method\":\"SendMessage\",\"params\":{\"message\":{\"messageId\":\"m1\",\"role\":\"ROLE_USER\",\"parts\":[{\"text\":\"x\"}]}}}",
            System.Text.Encoding.UTF8, contentType);
        if (origin != null) request.Headers.Add("Origin", origin);

        using HttpResponseMessage response = await http.SendAsync(request);

        Assert.That((int)response.StatusCode, Is.InRange(400, 499));
        Assert.That(_received, Is.Empty);
    }

    [Test]
    public async Task JsonRpcSendMessageRunsTheInstructionAndCompletesTheTask()
    {
        using HttpClient http = AuthorizedHttp();
        var client = new A2AClient(new Uri(HttpUrl(A2AServerHost.JsonRpcPath)), http);

        SendMessageResponse response = await client.SendMessageAsync(Request("build a wall"), CancellationToken.None);

        Assert.That(_received, Is.EqualTo((string[])["build a wall"]));
        Assert.That(response.Task, Is.Not.Null);
        Assert.That(response.Task.Status.State, Is.EqualTo(TaskState.Completed));
        Assert.That(response.Task.Status.Message.Parts[0].Text, Is.EqualTo("did: build a wall"));
    }

    [Test]
    public async Task GrpcSendMessageRunsTheInstructionAndCompletesTheTask()
    {
        using GrpcChannel channel = GrpcChannel.ForAddress(GrpcUrl());
        var client = new Protos.A2AService.A2AServiceClient(channel);
        var request = new Protos.SendMessageRequest { Message = new Protos.Message { MessageId = "m1", Role = Protos.Role.User } };
        request.Message.Parts.Add(new Protos.Part { Text = "how many colonists?" });

        Protos.SendMessageResponse response = await client.SendMessageAsync(request, GrpcAuth());

        Assert.That(_received, Is.EqualTo((string[])["how many colonists?"]));
        Assert.That(response.Task.Status.State, Is.EqualTo(Protos.TaskState.Completed));
        Assert.That(response.Task.Status.Message.Parts[0].Text, Is.EqualTo("did: how many colonists?"));

        Protos.Task fetched = await client.GetTaskAsync(new Protos.GetTaskRequest { Id = response.Task.Id }, GrpcAuth());
        Assert.That(fetched.Status.State, Is.EqualTo(Protos.TaskState.Completed));
    }

    [Test]
    public async Task RunnerFailureFailsTheTaskWithItsMessage()
    {
        _behavior = (_, _) => throw new InvalidOperationException("RimAgent is busy.");
        using HttpClient http = AuthorizedHttp();
        var client = new A2AClient(new Uri(HttpUrl(A2AServerHost.JsonRpcPath)), http);

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
        using HttpClient http = AuthorizedHttp();
        var client = new A2AClient(new Uri(HttpUrl(A2AServerHost.JsonRpcPath)), http);
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
        using HttpClient http = AuthorizedHttp();
        var client = new A2AClient(new Uri(HttpUrl(A2AServerHost.JsonRpcPath)), http);
        using var giveUp = new CancellationTokenSource();

        Task<SendMessageResponse> blocking = client.SendMessageAsync(Request("long job"), giveUp.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        await giveUp.CancelAsync();

        Assert.That(async () => await blocking, Throws.InstanceOf<OperationCanceledException>());
        await observedCancel.Task.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
    }

    [Test]
    public async Task ForeignHostHeaderIsRejected()
    {
        using HttpClient http = AuthorizedHttp();
        using var request = new HttpRequestMessage(HttpMethod.Get, HttpUrl("/.well-known/agent-card.json"));
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
        using GrpcChannel channel = GrpcChannel.ForAddress(GrpcUrl());
        var client = new Protos.A2AService.A2AServiceClient(channel);
        var request = new Protos.SendMessageRequest { Message = new Protos.Message { MessageId = "m2", Role = Protos.Role.User } };
        request.Message.Parts.Add(new Protos.Part { Text = "status" });

        var states = new List<Protos.TaskState>();
        using var call = client.SendStreamingMessage(request, GrpcAuth());
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
        using HttpClient http = AuthorizedHttp();
        AgentCard card = await new A2ACardResolver(new Uri(HttpUrl()), http).GetAgentCardAsync();

        Assert.That(card.Name, Is.EqualTo("RimAgent"));
        Assert.That(card.SupportedInterfaces.ConvertAll(i => i.ProtocolBinding),
            Is.EquivalentTo((string[])[ProtocolBindingNames.JsonRpc, ProtocolBindingNames.Grpc]));
    }

    [Test]
    public async Task BearerTokenProtectsEndpointAndRejectsOversizedInstruction()
    {
        int httpPort = FreePort();
        int grpcPort = FreePort();
        await using IAsyncDisposable server = await A2AServerHost.StartAsync(
            AppContext.BaseDirectory, httpPort, grpcPort, "test",
            (text, _) => Task.FromResult("ok: " + text),
            bearerToken: "secret-token-123");

        string url = string.Create(CultureInfo.InvariantCulture, $"http://localhost:{httpPort}{A2AServerHost.JsonRpcPath}");
        using var http = new HttpClient();

        using var unauthReq = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"method\":\"SendMessage\",\"params\":{}}", System.Text.Encoding.UTF8, "application/json")
        };
        using HttpResponseMessage unauthResp = await http.SendAsync(unauthReq);
        Assert.That(unauthResp.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "secret-token-123");
        var client = new A2AClient(new Uri(url), http);
        SendMessageResponse resp = await client.SendMessageAsync(Request(new string('a', A2AServerHost.MaxInstructionChars + 1)), CancellationToken.None);
        Assert.That(resp.Task.Status.State, Is.EqualTo(TaskState.Rejected));
    }

    /// <summary>沒有權杖就不驗證的話，本機任何程式都能指揮殖民地。</summary>
    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void StartWithoutTokenIsRefused(string token)
    {
        Assert.That(async () => await A2AServerHost.StartAsync(AppContext.BaseDirectory, FreePort(), FreePort(), "test",
            (text, _) => Task.FromResult(text), token), Throws.InstanceOf<ArgumentException>());
    }

    [TestCase("bearer")]
    [TestCase("BEARER")]
    public async Task BearerSchemeIsCaseInsensitive(string scheme)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", scheme + " " + Token);
        var client = new A2AClient(new Uri(HttpUrl(A2AServerHost.JsonRpcPath)), http);

        SendMessageResponse response = await client.SendMessageAsync(Request("hello"), CancellationToken.None);

        Assert.That(response.Task.Status.State, Is.EqualTo(TaskState.Completed));
    }

    [Test]
    public async Task WrongTokenIsRejected()
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token + "x");
        using var request = new HttpRequestMessage(HttpMethod.Post, HttpUrl(A2AServerHost.JsonRpcPath))
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
        };

        using HttpResponseMessage response = await http.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(_received, Is.Empty);
    }

    [Test]
    public void SettingsGeneratesAndRegeneratesA2ATokenAndCancelWhenIdleIsSafe()
    {
        var settings = new RimAgentSettings();
        string first = settings.EnsureA2AToken();
        Assert.That(first, Has.Length.EqualTo(64));
        Assert.That(settings.EnsureA2AToken(), Is.EqualTo(first));
        settings.RegenerateA2AToken();
        Assert.That(settings.a2aToken, Is.Not.EqualTo(first).And.Length.EqualTo(64));
        Assert.DoesNotThrow(A2AServerController.CancelActiveInstruction);
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
