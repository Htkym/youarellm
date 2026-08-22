using System.Net.Http.Json;
using System.Net;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using YouAreLlm.Core;
using YouAreLlm.Web.Mcp;
using YouAreLlm.Web.Research;

namespace YouAreLlm.Tests;

public sealed class OpenAiEndpointTests
{
    [Fact]
    public async Task ModelsEndpointReturnsHumanModel()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var response = await client.GetFromJsonAsync<ModelsEnvelope>("/v1/models");

        Assert.NotNull(response);
        Assert.Contains(response.Data, model => model.Id == "human");
    }

    [Fact]
    public async Task ConsoleLogRedirectTargetsTheReferringDashboardResource()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/dashboard/consolelogs/copilot-completions");
        request.Headers.Referrer = new Uri("https://localhost:18888/resources");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(
            "https://localhost:18888/consolelogs/resource/copilot-completions",
            response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task NonStreamingChatCompletionWaitsForHumanResponse()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<PendingRequestStore>();

        var postTask = client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "human",
            messages = new[] { new { role = "user", content = "hello" } },
            stream = false,
        });

        var requestId = await WaitForPendingRequestAsync(store);
        Assert.True(store.TryCompleteText(requestId, "human response"));

        var response = await postTask;
        var body = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();
        Assert.Contains("human response", body);
        using var document = JsonDocument.Parse(body);
        AssertPositiveUsage(document.RootElement.GetProperty("usage"), "prompt_tokens", "completion_tokens");
    }

    [Fact]
    public async Task ChatCompletionEmitsSafeGenAiSpan()
    {
        var activityStarted = new TaskCompletionSource<Activity>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "YouAreLlm.OpenAI",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity =>
            {
                if (activity.OperationName == "gen_ai.chat")
                {
                    activityStarted.TrySetResult(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<PendingRequestStore>();

        var postTask = client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "human",
            messages = new[] { new { role = "user", content = "trace this request" } },
            stream = false,
        });

        var requestId = await WaitForPendingRequestAsync(store);
        Assert.True(store.TryCompleteText(requestId, "human response"));
        (await postTask).EnsureSuccessStatusCode();

        var activity = await activityStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("chat", activity.GetTagItem("gen_ai.operation.name"));
        Assert.Equal("openai", activity.GetTagItem("gen_ai.provider.name"));
        Assert.Equal("human", activity.GetTagItem("gen_ai.request.model"));
        Assert.Equal("chat_completions", activity.GetTagItem("youarellm.protocol"));
        Assert.Equal(1, activity.GetTagItem("youarellm.input.message_count"));
        Assert.Equal("text", activity.GetTagItem("youarellm.completion.kind"));
        Assert.Null(activity.GetTagItem("gen_ai.input.messages"));
        Assert.Null(activity.GetTagItem("gen_ai.output.messages"));
    }

    [Fact]
    public async Task ChatCompletionRecognizesTaskRequestWithEmptyToolsAsBackground()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<PendingRequestStore>();

        var postTask = client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "any-model",
            messages = new[]
            {
                new { role = "system", content = "Task: Classify the current message." },
                new { role = "user", content = "Hello" },
            },
            tools = Array.Empty<object>(),
        });

        var requestId = await WaitForPendingRequestAsync(store);
        var pending = Assert.Single(store.GetPending());
        Assert.True(pending.IsBackgroundRequest);
        Assert.Contains("\"messages\"", pending.RawRequestJson);
        Assert.Contains("\"tools\":[]", pending.RawRequestJson);
        Assert.True(store.TryCompleteText(requestId, "classification result"));

        (await postTask).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task ChatCompletionPreservesToolItemTypesForOperatorDisplay()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<PendingRequestStore>();

        var postTask = client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "human",
            messages = new object[]
            {
                new
                {
                    role = "assistant",
                    content = (string?)null,
                    tool_calls = new[]
                    {
                        new
                        {
                            id = "call_123",
                            type = "function",
                            function = new { name = "powershell", arguments = """{"command":"Get-Date"}""" },
                        },
                    },
                },
                new
                {
                    role = "tool",
                    tool_call_id = "call_123",
                    content = "tool result",
                },
            },
        });

        var requestId = await WaitForPendingRequestAsync(store);
        var pending = Assert.Single(store.GetPending());
        Assert.Contains("\"messages\"", pending.RawRequestJson);
        Assert.Contains("\"tool_calls\"", pending.RawRequestJson);
        Assert.Contains("\"tool_call_id\"", pending.RawRequestJson);
        Assert.Collection(
            pending.Messages,
            message => Assert.Equal("tool_calls", message.ItemType),
            message => Assert.Equal("tool_result", message.ItemType));
        Assert.True(store.TryCompleteText(requestId, "human response"));

        (await postTask).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task StreamingChatCompletionWritesDeltasAndDone()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<PendingRequestStore>();

        var postTask = client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "human",
            messages = new[] { new { role = "user", content = "stream please" } },
            stream = true,
        });

        var requestId = await WaitForPendingRequestAsync(store);
        Assert.True(store.TryAddDelta(requestId, "partial "));
        Assert.True(store.TryCompleteText(requestId, "final"));

        var response = await postTask;
        var body = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();
        Assert.Contains("partial", body);
        Assert.Contains("final", body);
        Assert.Contains("[DONE]", body);
        Assert.DoesNotContain("\"usage\":", body);
    }

    [Fact]
    public async Task StreamingChatCompletionIncludesUsageWhenRequested()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<PendingRequestStore>();

        var postTask = client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "human",
            messages = new[] { new { role = "user", content = "count this streamed response" } },
            stream = true,
            stream_options = new { include_usage = true },
        });

        var requestId = await WaitForPendingRequestAsync(store);
        Assert.True(store.TryCompleteText(requestId, "streamed human response"));

        var response = await postTask;
        var body = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();
        AssertPositiveUsage(GetStreamingUsage(body), "prompt_tokens", "completion_tokens");
    }

    [Fact]
    public async Task NonStreamingResponseMapsInputItemsAndReturnsOutputText()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<PendingRequestStore>();

        var postTask = client.PostAsJsonAsync("/v1/responses", new
        {
            model = "human",
            instructions = "Follow the operator.",
            input = new object[]
            {
                new { type = "message", role = "user", content = "hello" },
                new
                {
                    type = "function_call",
                    call_id = "call_123",
                    name = "powershell",
                    arguments = """{"command":"Get-Date"}""",
                },
                new { type = "function_call_output", call_id = "call_123", output = "tool result" },
            },
        });

        var requestId = await WaitForPendingRequestAsync(store);
        var pending = Assert.Single(store.GetPending());
        Assert.Contains("\"input\"", pending.RawRequestJson);
        Assert.Contains("\"function_call\"", pending.RawRequestJson);
        Assert.Contains("\"function_call_output\"", pending.RawRequestJson);
        Assert.Collection(
            pending.Messages,
            message => Assert.Equal(new ChatMessage("system", "Follow the operator."), message),
            message => Assert.Equal(new ChatMessage("user", "hello"), message),
            message =>
            {
                Assert.Equal("assistant", message.Role);
                Assert.Equal("function_call", message.ItemType);
                Assert.Contains("call_123", message.Content);
            },
            message =>
            {
                Assert.Equal("tool", message.Role);
                Assert.Equal("function_call_output", message.ItemType);
                Assert.Contains("call_123", message.Content);
                Assert.Contains("tool result", message.Content);
            });
        Assert.True(store.TryCompleteText(requestId, "human response"));

        var response = await postTask;
        var body = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();
        Assert.Contains("\"object\":\"response\"", body);
        Assert.Contains("\"type\":\"output_text\"", body);
        Assert.Contains("human response", body);
        using var document = JsonDocument.Parse(body);
        AssertPositiveUsage(document.RootElement.GetProperty("usage"), "input_tokens", "output_tokens");
    }

    [Fact]
    public async Task NonStreamingResponseReturnsFunctionCall()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<PendingRequestStore>();

        var postTask = client.PostAsJsonAsync("/v1/responses", new
        {
            model = "human",
            input = "run the command",
            tools = new[]
            {
                new
                {
                    type = "function",
                    name = "powershell",
                    parameters = new { type = "object" },
                },
            },
        });

        var requestId = await WaitForPendingRequestAsync(store);
        Assert.True(store.TryCompleteTool(
            requestId,
            new FunctionCallItem("call_123", "powershell", """{"command":"dotnet hello.cs"}""")));

        var response = await postTask;
        var body = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();
        Assert.Contains("\"type\":\"function_call\"", body);
        Assert.Contains("\"call_id\":\"call_123\"", body);
        Assert.Contains("\"name\":\"powershell\"", body);
        using var document = JsonDocument.Parse(body);
        AssertPositiveUsage(document.RootElement.GetProperty("usage"), "input_tokens", "output_tokens");
    }

    [Fact]
    public async Task ResponsePreviousResponseIdCarriesForwardEarlierConversation()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<PendingRequestStore>();

        var firstPostTask = client.PostAsJsonAsync("/v1/responses", new
        {
            model = "human",
            input = "first question",
        });
        var firstRequestId = await WaitForPendingRequestAsync(store);
        Assert.True(store.TryCompleteText(firstRequestId, "first response"));
        await firstPostTask;

        var secondPostTask = client.PostAsJsonAsync("/v1/responses", new
        {
            model = "human",
            previous_response_id = $"resp_{firstRequestId}",
            input = "follow-up question",
        });
        var secondRequestId = await WaitForPendingRequestAsync(store);
        var pending = Assert.Single(store.GetPending());
        Assert.Equal("Responses", pending.Protocol);
        Assert.Collection(
            pending.Messages,
            message => Assert.Equal(new ChatMessage("user", "first question"), message),
            message => Assert.Equal(new ChatMessage("assistant", "first response"), message),
            message => Assert.Equal(new ChatMessage("user", "follow-up question"), message));
        Assert.True(store.TryCompleteText(secondRequestId, "second response"));

        (await secondPostTask).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task StreamingResponseWritesSemanticEvents()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<PendingRequestStore>();

        var postTask = client.PostAsJsonAsync("/v1/responses", new
        {
            model = "human",
            input = "stream please",
            stream = true,
        });

        var requestId = await WaitForPendingRequestAsync(store);
        Assert.True(store.TryAddDelta(requestId, "partial "));
        Assert.True(store.TryCompleteText(requestId, "final"));

        var response = await postTask;
        var body = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();
        Assert.Contains("event: response.created", body);
        Assert.Contains("event: response.output_text.delta", body);
        Assert.Contains("event: response.completed", body);
        Assert.Contains("partial", body);
        Assert.Contains("final", body);
        Assert.DoesNotContain("[DONE]", body);
        AssertPositiveUsage(GetStreamingUsage(body), "input_tokens", "output_tokens");
    }

    [Fact]
    public async Task ChatCompletionArchivesRawRequestPayload()
    {
        var captureDirectory = Path.Combine(Path.GetTempPath(), $"youarellm-tests-{Guid.NewGuid():N}");
        try
        {
            await using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                    builder.ConfigureAppConfiguration((_, configuration) =>
                        configuration.AddInMemoryCollection(
                            new Dictionary<string, string?>
                            {
                                ["ResearchCapture:Directory"] = captureDirectory,
                            })));
            using var client = factory.CreateClient();
            var store = factory.Services.GetRequiredService<PendingRequestStore>();

            client.DefaultRequestHeaders.Add(ResearchCaptureOptions.SelfAuthoredRequestHeader, "true");
            var postTask = client.PostAsJsonAsync("/v1/chat/completions", new
            {
                model = "human",
                messages = new[] { new { role = "user", content = "archive this prompt" } },
                stream = false,
            });

            var requestId = await WaitForPendingRequestAsync(store);
            Assert.True(store.TryCompleteText(requestId, "human response"));
            await postTask;

            var archive = Assert.Single(Directory.GetFiles(captureDirectory, "*.json"));
            var payload = await File.ReadAllTextAsync(archive);
            Assert.Contains("\"model\":\"human\"", payload);
            Assert.Contains("archive this prompt", payload);
        }
        finally
        {
            if (Directory.Exists(captureDirectory))
            {
                Directory.Delete(captureDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ChatCompletionDoesNotArchiveRequestWithoutSelfAuthoredHeader()
    {
        var captureDirectory = Path.Combine(Path.GetTempPath(), $"youarellm-tests-{Guid.NewGuid():N}");
        try
        {
            await using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                    builder.ConfigureAppConfiguration((_, configuration) =>
                        configuration.AddInMemoryCollection(
                            new Dictionary<string, string?>
                            {
                                ["ResearchCapture:Directory"] = captureDirectory,
                            })));
            using var client = factory.CreateClient();
            var store = factory.Services.GetRequiredService<PendingRequestStore>();

            var postTask = client.PostAsJsonAsync("/v1/chat/completions", new
            {
                model = "human",
                messages = new[] { new { role = "user", content = "do not archive this prompt" } },
                stream = false,
            });

            var requestId = await WaitForPendingRequestAsync(store);
            Assert.True(store.TryCompleteText(requestId, "human response"));
            await postTask;

            Assert.False(Directory.Exists(captureDirectory));
        }
        finally
        {
            if (Directory.Exists(captureDirectory))
            {
                Directory.Delete(captureDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task FleetRunStartsWorkersInParallelAndCompletesParentSynthesis()
    {
        var store = new PendingRequestStore();
        var fleetRuns = new FleetRunStore(store);

        var run = fleetRuns.StartRun("Verify the self-authored greeting fixture.");

        Assert.Equal(FleetRunStatus.AwaitingWorkers, run.Status);
        Assert.Equal(2, run.Workers.Count);

        var workers = store.GetPending();
        Assert.Equal(2, workers.Count);
        foreach (var worker in workers)
        {
            Assert.True(store.TryCompleteText(worker.RequestId, $"Evidence from {worker.RequestId}."));
        }

        await WaitUntilAsync(() => fleetRuns.GetRuns().Single().ParentRequestId is not null);
        var parent = Assert.Single(store.GetPending());
        Assert.True(store.TryCompleteText(parent.RequestId, "evidence: combined\naction: none\nverification: tests"));

        await WaitUntilAsync(() => fleetRuns.GetRuns().Single().Status == FleetRunStatus.Completed);

        var completed = Assert.Single(fleetRuns.GetRuns());
        Assert.Equal(FleetRunStatus.Completed, completed.Status);
        Assert.All(completed.Workers, worker => Assert.Equal(FleetWorkerStatus.Completed, worker.Status));
        Assert.NotNull(completed.ParentResponse);
    }

    [Fact]
    public async Task HumanDelegationToolReturnsOperatorResponse()
    {
        var store = new PendingRequestStore();
        var tool = new HumanDelegationTools(store, NullLogger<HumanDelegationTools>.Instance);

        var delegation = tool.DelegateToHumanAsync("Summarize the fixture.", CancellationToken.None);

        var requestId = await WaitForPendingRequestAsync(store);
        Assert.True(store.TryCompleteText(requestId, "The fixture contains a greeting."));

        Assert.Equal("The fixture contains a greeting.", await delegation);
    }

    [Fact]
    public async Task HumanDelegationToolRejectsOversizedTask()
    {
        var store = new PendingRequestStore();
        var tool = new HumanDelegationTools(store, NullLogger<HumanDelegationTools>.Instance);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            tool.DelegateToHumanAsync(new string('x', 4_001), CancellationToken.None));
        Assert.Empty(store.GetPending());
    }

    [Fact]
    public void PendingSnapshotIdentifiesBackgroundRequestWithoutModelDependency()
    {
        var auxiliaryRequest = new PendingRequestSnapshot(
            "request-id",
            [
                new ChatMessage("system", "Task: Detect whether the CURRENT MESSAGE expresses frustration."),
                new ChatMessage("user", "Please read the file."),
            ],
            "future-background-model",
            DateTimeOffset.UtcNow,
            null);

        Assert.True(auxiliaryRequest.IsBackgroundRequest);
        Assert.True((auxiliaryRequest with { Model = "gpt-5.6-terra" }).IsBackgroundRequest);
        Assert.True((auxiliaryRequest with { ToolsJson = "[]" }).IsBackgroundRequest);
        Assert.False((auxiliaryRequest with { ToolsJson = """[{"type":"function","name":"powershell"}]""" }).IsBackgroundRequest);
    }

    [Fact]
    public void PendingSnapshotDoesNotClassifyModelNameAsBackgroundRequest()
    {
        var auxiliaryRequest = new PendingRequestSnapshot(
            "request-id",
            [new ChatMessage("user", "Name this conversation.")],
            "gpt-5.4-nano",
            DateTimeOffset.UtcNow,
            "[]");

        Assert.False(auxiliaryRequest.IsBackgroundRequest);
    }

    [Fact]
    public void CompletedTextHistoryPreservesAccumulatedOutputForUsage()
    {
        var store = new PendingRequestStore();
        var request = store.Add([new ChatMessage("user", "count the response")], "human");

        Assert.True(store.TryAddDelta(request.RequestId, "partial "));
        Assert.True(store.TryCompleteText(request.RequestId, "final"));

        var completed = Assert.Single(store.GetHistory());
        Assert.Equal("partial final", completed.Response);
        Assert.Equal(completed.Response, completed.UsageOutput);
    }

    [Fact]
    public void CompletedToolHistoryPreservesSerializedOutputForUsage()
    {
        var store = new PendingRequestStore();
        var request = store.Add([new ChatMessage("user", "call a tool")], "human");
        var toolCall = new FunctionCallItem("call_123", "powershell", """{"command":"Get-Date"}""");

        Assert.True(store.TryCompleteTool(request.RequestId, toolCall));

        var completed = Assert.Single(store.GetHistory());
        Assert.Equal(
            JsonSerializer.Serialize(toolCall, toolCall.GetType()),
            completed.UsageOutput);
    }

    private static async Task<string> WaitForPendingRequestAsync(PendingRequestStore store)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!cts.IsCancellationRequested)
        {
            var request = store.GetPending().FirstOrDefault();
            if (request is not null)
            {
                return request.RequestId;
            }

            await Task.Delay(25, cts.Token);
        }

        throw new TimeoutException("No pending request was created.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!cts.IsCancellationRequested)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25, cts.Token);
        }

        throw new TimeoutException("Expected condition was not met.");
    }

    private static JsonElement GetStreamingUsage(string body)
    {
        var usagePayload = body
            .Split("data: ", StringSplitOptions.RemoveEmptyEntries)
            .Select(chunk => chunk.Split('\n', 2)[0])
            .Last(chunk => chunk.Contains("\"usage\":", StringComparison.Ordinal));
        using var document = JsonDocument.Parse(usagePayload);
        var root = document.RootElement;
        var usage = root.TryGetProperty("usage", out var directUsage)
            ? directUsage
            : root.GetProperty("response").GetProperty("usage");
        return usage.Clone();
    }

    private static void AssertPositiveUsage(JsonElement usage, string inputProperty, string outputProperty)
    {
        var inputTokens = usage.GetProperty(inputProperty).GetInt32();
        var outputTokens = usage.GetProperty(outputProperty).GetInt32();

        Assert.True(inputTokens > 0);
        Assert.True(outputTokens > 0);
        Assert.Equal(inputTokens + outputTokens, usage.GetProperty("total_tokens").GetInt32());
    }

    private sealed record ModelsEnvelope(IReadOnlyList<ModelEnvelope> Data);

    private sealed record ModelEnvelope(string Id);
}
