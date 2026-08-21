using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using YouAreLlm.Core;
using YouAreLlm.Web.Research;

namespace YouAreLlm.Web.Api;

public static class OpenAiEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static RouteGroupBuilder MapOpenAiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/v1")
            .WithTags("OpenAI compatibility");

        group.MapGet("/models", GetModels)
            .WithName("ListModels")
            .WithSummary("List available models")
            .WithDescription("Returns the local human-in-the-loop model for Copilot CLI BYOK.");

        group.MapPost("/chat/completions", CreateChatCompletionAsync)
            .WithName("CreateChatCompletion")
            .WithSummary("Create a chat completion")
            .WithDescription("OpenAI-compatible Chat Completions endpoint backed by a human operator.");

        group.MapPost("/responses", CreateResponseAsync)
            .WithName("CreateResponse")
            .WithSummary("Create a response")
            .WithDescription("OpenAI-compatible Responses endpoint backed by a human operator.");

        group.MapMethods("/{*path}", ["OPTIONS"], Options)
            .ExcludeFromDescription();

        return group;
    }

    private static IResult Options() => Results.NoContent();

    private static bool IsSelfAuthoredRequest(HttpContext httpContext)
        => string.Equals(
            httpContext.Request.Headers[ResearchCaptureOptions.SelfAuthoredRequestHeader],
            "true",
            StringComparison.OrdinalIgnoreCase);

    private static Ok<ModelsResponse> GetModels()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return TypedResults.Ok(new ModelsResponse(
            "list",
            [new ModelInfo("human", "model", now, "youarellm")]));
    }

    private static async Task<IResult> CreateChatCompletionAsync(
        JsonDocument rawRequest,
        PendingRequestStore store,
        TokenUsageEstimator tokenUsageEstimator,
        IRawPromptArchive rawPromptArchive,
        ILoggerFactory loggerFactory,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        ChatCompletionRequest? request;
        try
        {
            request = rawRequest.Deserialize<ChatCompletionRequest>(JsonOptions);
        }
        catch (JsonException ex)
        {
            return TypedResults.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }

        if (request is null)
        {
            return TypedResults.Problem("Request body must contain a JSON object.", statusCode: StatusCodes.Status400BadRequest);
        }

        var model = string.IsNullOrWhiteSpace(request.Model) ? "human" : request.Model;
        var messages = ChatCompletionMapper.ToCoreMessages(request.Messages);
        using var activity = CopilotRequestTelemetry.Start(
            "chat_completions",
            model,
            request.Stream,
            messages.Count,
            request.Tools);
        var pendingCreation = await CreatePendingRequestAsync(
            messages,
            model,
            request.Tools?.GetRawText(),
            "Chat Completions",
            null,
            rawRequest.RootElement.GetRawText(),
            rawRequest,
            store,
            rawPromptArchive,
            loggerFactory,
            httpContext,
            cancellationToken);
        if (pendingCreation.Error is not null)
        {
            CopilotRequestTelemetry.Fail(activity, "request_creation_failed");
            return pendingCreation.Error;
        }

        var pending = pendingCreation.Pending!;

        var created = pending.CreatedAt.ToUnixTimeSeconds();
        using var requestAbortedRegistration = cancellationToken.Register(() =>
            store.TryCancel(pending.RequestId, new OperationCanceledException("Client disconnected.")));

        if (request.Stream)
        {
            var streamingCompletion = await WriteStreamingResponseAsync(
                httpContext,
                pending,
                created,
                model,
                request.StreamOptions?.IncludeUsage is true,
                tokenUsageEstimator,
                cancellationToken);
            if (streamingCompletion is not null)
            {
                CopilotRequestTelemetry.Complete(activity, streamingCompletion);
            }
            else
            {
                CopilotRequestTelemetry.Fail(activity, "timeout");
            }

            return Results.Empty;
        }

        HumanCompletion completion;
        try
        {
            completion = await pending.Completion.WaitAsync(cancellationToken);
        }
        catch (TimeoutException ex)
        {
            CopilotRequestTelemetry.Fail(activity, "timeout");
            return TypedResults.Problem(ex.Message, statusCode: StatusCodes.Status504GatewayTimeout);
        }

        CopilotRequestTelemetry.Complete(activity, completion);
        var usage = tokenUsageEstimator.Estimate(pending.Messages, pending.ToolsJson, completion);
        return completion switch
        {
            TextCompletion text => TypedResults.Json(CreateCompletionResponse(pending.RequestId, created, model, text.Text, usage), options: JsonOptions),
            ToolCompletion tool => TypedResults.Json(CreateToolCompletionResponse(pending.RequestId, created, model, tool.ToolCall, usage), options: JsonOptions),
            _ => TypedResults.Problem("Unsupported completion result."),
        };
    }

    private static async Task<IResult> CreateResponseAsync(
        JsonDocument rawRequest,
        PendingRequestStore store,
        TokenUsageEstimator tokenUsageEstimator,
        IRawPromptArchive rawPromptArchive,
        ILoggerFactory loggerFactory,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        ResponsesRequest? request;
        try
        {
            request = rawRequest.Deserialize<ResponsesRequest>(JsonOptions);
        }
        catch (JsonException ex)
        {
            return TypedResults.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }

        if (request is null)
        {
            return TypedResults.Problem("Request body must contain a JSON object.", statusCode: StatusCodes.Status400BadRequest);
        }

        var model = string.IsNullOrWhiteSpace(request.Model) ? "human" : request.Model;
        if (!TryBuildResponseMessages(
                store,
                request.PreviousResponseId,
                ResponsesMapper.ToCoreMessages(request.Input, request.Instructions),
                out var messages))
        {
            return TypedResults.Problem(
                $"Unknown previous_response_id: {request.PreviousResponseId}",
                statusCode: StatusCodes.Status404NotFound);
        }

        using var activity = CopilotRequestTelemetry.Start(
            "responses",
            model,
            request.Stream,
            messages.Count,
            request.Tools);
        var pendingCreation = await CreatePendingRequestAsync(
            messages,
            model,
            request.Tools?.GetRawText(),
            "Responses",
            request.PreviousResponseId,
            rawRequest.RootElement.GetRawText(),
            rawRequest,
            store,
            rawPromptArchive,
            loggerFactory,
            httpContext,
            cancellationToken);
        if (pendingCreation.Error is not null)
        {
            CopilotRequestTelemetry.Fail(activity, "request_creation_failed");
            return pendingCreation.Error;
        }

        var pending = pendingCreation.Pending!;
        var created = pending.CreatedAt.ToUnixTimeSeconds();
        using var requestAbortedRegistration = cancellationToken.Register(() =>
            store.TryCancel(pending.RequestId, new OperationCanceledException("Client disconnected.")));

        if (request.Stream)
        {
            var streamingCompletion = await WriteResponsesStreamingResponseAsync(
                httpContext,
                pending,
                created,
                model,
                request.PreviousResponseId,
                tokenUsageEstimator,
                cancellationToken);
            if (streamingCompletion is not null)
            {
                CopilotRequestTelemetry.Complete(activity, streamingCompletion);
            }
            else
            {
                CopilotRequestTelemetry.Fail(activity, "timeout");
            }

            return Results.Empty;
        }

        try
        {
            var completion = await pending.Completion.WaitAsync(cancellationToken);
            CopilotRequestTelemetry.Complete(activity, completion);
            var usage = tokenUsageEstimator.Estimate(pending.Messages, pending.ToolsJson, completion);
            return completion switch
            {
                TextCompletion text => TypedResults.Json(
                    CreateResponseCompletion(pending.RequestId, created, model, request.PreviousResponseId, text.Text, usage),
                    options: JsonOptions),
                ToolCompletion tool => TypedResults.Json(
                    CreateResponseToolCompletion(pending.RequestId, created, model, request.PreviousResponseId, tool.ToolCall, usage),
                    options: JsonOptions),
                _ => TypedResults.Problem("Unsupported completion result."),
            };
        }
        catch (TimeoutException ex)
        {
            CopilotRequestTelemetry.Fail(activity, "timeout");
            return TypedResults.Problem(ex.Message, statusCode: StatusCodes.Status504GatewayTimeout);
        }
    }

    private static async Task<PendingRequestCreation> CreatePendingRequestAsync(
        IReadOnlyList<ChatMessage> messages,
        string model,
        string? toolsJson,
        string protocol,
        string? previousResponseId,
        string rawRequestJson,
        JsonDocument rawRequest,
        PendingRequestStore store,
        IRawPromptArchive rawPromptArchive,
        ILoggerFactory loggerFactory,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var pending = store.Add(
            messages,
            model,
            toolsJson,
            protocol: protocol,
            previousResponseId: previousResponseId,
            rawRequestJson: rawRequestJson);
        if (!IsSelfAuthoredRequest(httpContext))
        {
            return new PendingRequestCreation(pending, null);
        }

        var logger = loggerFactory.CreateLogger(typeof(OpenAiEndpoints));
        try
        {
            await rawPromptArchive.ArchiveAsync(
                pending.RequestId,
                rawRequest.RootElement.GetRawText(),
                cancellationToken);
        }
        catch (IOException ex)
        {
            store.TryCancel(pending.RequestId, ex);
            logger.LogError(ex, "Failed to archive raw prompt for request {RequestId}.", pending.RequestId);
            return new PendingRequestCreation(
                null,
                TypedResults.Problem("Unable to archive the raw prompt.", statusCode: StatusCodes.Status500InternalServerError));
        }
        catch (UnauthorizedAccessException ex)
        {
            store.TryCancel(pending.RequestId, ex);
            logger.LogError(ex, "Failed to archive raw prompt for request {RequestId}.", pending.RequestId);
            return new PendingRequestCreation(
                null,
                TypedResults.Problem("Unable to archive the raw prompt.", statusCode: StatusCodes.Status500InternalServerError));
        }

        return new PendingRequestCreation(pending, null);
    }

    private static bool TryBuildResponseMessages(
        PendingRequestStore store,
        string? previousResponseId,
        IReadOnlyList<ChatMessage> currentMessages,
        out IReadOnlyList<ChatMessage> messages)
    {
        if (string.IsNullOrWhiteSpace(previousResponseId))
        {
            messages = currentMessages;
            return true;
        }

        const string responseIdPrefix = "resp_";
        if (!previousResponseId.StartsWith(responseIdPrefix, StringComparison.Ordinal) ||
            !store.TryGetCompletedRequest(previousResponseId[responseIdPrefix.Length..], out var previous) ||
            previous is null)
        {
            messages = [];
            return false;
        }

        messages = previous.Messages
            .Where(message => message.Role != "system")
            .Append(new ChatMessage("assistant", previous.Response))
            .Concat(currentMessages)
            .ToArray();
        return true;
    }

    private static async Task<HumanCompletion?> WriteStreamingResponseAsync(
        HttpContext httpContext,
        PendingRequest pending,
        long created,
        string model,
        bool includeUsage,
        TokenUsageEstimator tokenUsageEstimator,
        CancellationToken cancellationToken)
    {
        httpContext.Response.Headers.CacheControl = "no-cache";
        httpContext.Response.Headers.Connection = "keep-alive";
        httpContext.Response.ContentType = "text/event-stream";

        HumanCompletion? completion = null;
        try
        {
            await WriteSseAsync(httpContext, CreateRoleChunk(pending.RequestId, created, model), cancellationToken);
            var completionTask = pending.Completion;

            while (!completionTask.IsCompleted)
            {
                var deltaAvailableTask = pending.DeltaReader.WaitToReadAsync(cancellationToken).AsTask();
                var heartbeatTask = Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
                var completedTask = await Task.WhenAny(completionTask, deltaAvailableTask, heartbeatTask);

                if (completedTask == deltaAvailableTask && await deltaAvailableTask)
                {
                    await DrainDeltasAsync(httpContext, pending, created, model, cancellationToken);
                }
                else if (completedTask == heartbeatTask)
                {
                    await httpContext.Response.WriteAsync(": ping\n\n", cancellationToken);
                    await httpContext.Response.Body.FlushAsync(cancellationToken);
                }
            }

            await DrainDeltasAsync(httpContext, pending, created, model, cancellationToken);

            completion = await completionTask;
            if (completion is ToolCompletion tool)
            {
                await WriteSseAsync(httpContext, CreateToolDeltaChunk(pending.RequestId, created, model, tool.ToolCall), cancellationToken);
                await WriteSseAsync(httpContext, CreateFinishChunk(pending.RequestId, created, model, "tool_calls"), cancellationToken);
            }
            else
            {
                await WriteSseAsync(httpContext, CreateFinishChunk(pending.RequestId, created, model, "stop"), cancellationToken);
            }

            if (includeUsage)
            {
                var usage = tokenUsageEstimator.Estimate(pending.Messages, pending.ToolsJson, completion);
                await WriteSseAsync(httpContext, CreateUsageChunk(pending.RequestId, created, model, usage), cancellationToken);
            }
        }
        catch (TimeoutException)
        {
            await WriteSseAsync(httpContext, CreateTextDeltaChunk(pending.RequestId, created, model, "[youarellm: request timed out]"), cancellationToken);
            await WriteSseAsync(httpContext, CreateFinishChunk(pending.RequestId, created, model, "stop"), cancellationToken);
        }

        await httpContext.Response.WriteAsync("data: [DONE]\n\n", cancellationToken);
        await httpContext.Response.Body.FlushAsync(cancellationToken);
        return completion;
    }

    private static async Task<HumanCompletion?> WriteResponsesStreamingResponseAsync(
        HttpContext httpContext,
        PendingRequest pending,
        long created,
        string model,
        string? previousResponseId,
        TokenUsageEstimator tokenUsageEstimator,
        CancellationToken cancellationToken)
    {
        httpContext.Response.Headers.CacheControl = "no-cache";
        httpContext.Response.Headers.Connection = "keep-alive";
        httpContext.Response.ContentType = "text/event-stream";

        await WriteResponsesSseAsync(
            httpContext,
            "response.created",
            CreateResponseInProgress(pending.RequestId, created, model, previousResponseId),
            cancellationToken);

        HumanCompletion? completion = null;
        try
        {
            var completionTask = pending.Completion;
            var messageId = $"msg_{pending.RequestId}";
            var textStarted = false;

            while (!completionTask.IsCompleted)
            {
                var deltaAvailableTask = pending.DeltaReader.WaitToReadAsync(cancellationToken).AsTask();
                var heartbeatTask = Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
                var completedTask = await Task.WhenAny(completionTask, deltaAvailableTask, heartbeatTask);

                if (completedTask == deltaAvailableTask && await deltaAvailableTask)
                {
                    textStarted = await WriteResponseTextDeltasAsync(
                        httpContext,
                        pending,
                        messageId,
                        textStarted,
                        cancellationToken);
                }
                else if (completedTask == heartbeatTask)
                {
                    await httpContext.Response.WriteAsync(": ping\n\n", cancellationToken);
                    await httpContext.Response.Body.FlushAsync(cancellationToken);
                }
            }

            textStarted = await WriteResponseTextDeltasAsync(
                httpContext,
                pending,
                messageId,
                textStarted,
                cancellationToken);

            completion = await completionTask;
            var usage = tokenUsageEstimator.Estimate(pending.Messages, pending.ToolsJson, completion);
            switch (completion)
            {
                case TextCompletion text:
                    if (!textStarted)
                    {
                        await WriteResponseTextStartAsync(httpContext, messageId, cancellationToken);
                    }

                    await WriteResponseTextCompleteAsync(
                        httpContext,
                        pending.RequestId,
                        created,
                        model,
                        previousResponseId,
                        messageId,
                        text.Text,
                        usage,
                        cancellationToken);
                    break;
                case ToolCompletion tool:
                    await WriteResponseToolCompleteAsync(
                        httpContext,
                        pending.RequestId,
                        created,
                        model,
                        previousResponseId,
                        tool.ToolCall,
                        usage,
                        cancellationToken);
                    break;
            }
        }
        catch (TimeoutException ex)
        {
            await WriteResponsesSseAsync(
                httpContext,
                "response.failed",
                CreateResponseFailed(pending.RequestId, created, model, previousResponseId, ex.Message),
                cancellationToken);
            return null;
        }

        return completion;
    }

    private static async Task<bool> WriteResponseTextDeltasAsync(
        HttpContext httpContext,
        PendingRequest pending,
        string messageId,
        bool textStarted,
        CancellationToken cancellationToken)
    {
        while (pending.DeltaReader.TryRead(out var delta))
        {
            if (!textStarted)
            {
                await WriteResponseTextStartAsync(httpContext, messageId, cancellationToken);
                textStarted = true;
            }

            await WriteResponsesSseAsync(
                httpContext,
                "response.output_text.delta",
                new
                {
                    type = "response.output_text.delta",
                    item_id = messageId,
                    output_index = 0,
                    content_index = 0,
                    delta,
                },
                cancellationToken);
        }

        return textStarted;
    }

    private static async Task WriteResponseTextStartAsync(
        HttpContext httpContext,
        string messageId,
        CancellationToken cancellationToken)
    {
        await WriteResponsesSseAsync(
            httpContext,
            "response.output_item.added",
            new
            {
                type = "response.output_item.added",
                output_index = 0,
                item = CreateResponseMessage(messageId, string.Empty, "in_progress"),
            },
            cancellationToken);
        await WriteResponsesSseAsync(
            httpContext,
            "response.content_part.added",
            new
            {
                type = "response.content_part.added",
                item_id = messageId,
                output_index = 0,
                content_index = 0,
                part = CreateResponseTextPart(string.Empty),
            },
            cancellationToken);
    }

    private static async Task WriteResponseTextCompleteAsync(
        HttpContext httpContext,
        string requestId,
        long created,
        string model,
        string? previousResponseId,
        string messageId,
        string text,
        TokenUsage usage,
        CancellationToken cancellationToken)
    {
        await WriteResponsesSseAsync(
            httpContext,
            "response.output_text.done",
            new
            {
                type = "response.output_text.done",
                item_id = messageId,
                output_index = 0,
                content_index = 0,
                text,
            },
            cancellationToken);
        await WriteResponsesSseAsync(
            httpContext,
            "response.content_part.done",
            new
            {
                type = "response.content_part.done",
                item_id = messageId,
                output_index = 0,
                content_index = 0,
                part = CreateResponseTextPart(text),
            },
            cancellationToken);
        await WriteResponsesSseAsync(
            httpContext,
            "response.output_item.done",
            new
            {
                type = "response.output_item.done",
                output_index = 0,
                item = CreateResponseMessage(messageId, text, "completed"),
            },
            cancellationToken);
        await WriteResponsesSseAsync(
            httpContext,
            "response.completed",
            CreateResponseCompleted(
                CreateResponseCompletion(requestId, created, model, previousResponseId, text, usage)),
            cancellationToken);
    }

    private static async Task WriteResponseToolCompleteAsync(
        HttpContext httpContext,
        string requestId,
        long created,
        string model,
        string? previousResponseId,
        ToolCallItem toolCall,
        TokenUsage usage,
        CancellationToken cancellationToken)
    {
        var function = (FunctionCallItem)toolCall;
        var functionId = $"fc_{requestId}";
        await WriteResponsesSseAsync(
            httpContext,
            "response.output_item.added",
            new
            {
                type = "response.output_item.added",
                output_index = 0,
                item = CreateResponseFunctionCall(functionId, function, "in_progress"),
            },
            cancellationToken);
        await WriteResponsesSseAsync(
            httpContext,
            "response.function_call_arguments.delta",
            new
            {
                type = "response.function_call_arguments.delta",
                item_id = functionId,
                output_index = 0,
                delta = function.Arguments,
            },
            cancellationToken);
        await WriteResponsesSseAsync(
            httpContext,
            "response.function_call_arguments.done",
            new
            {
                type = "response.function_call_arguments.done",
                item_id = functionId,
                output_index = 0,
                arguments = function.Arguments,
            },
            cancellationToken);
        await WriteResponsesSseAsync(
            httpContext,
            "response.output_item.done",
            new
            {
                type = "response.output_item.done",
                output_index = 0,
                item = CreateResponseFunctionCall(functionId, function, "completed"),
            },
            cancellationToken);
        await WriteResponsesSseAsync(
            httpContext,
            "response.completed",
            CreateResponseCompleted(
                CreateResponseToolCompletion(requestId, created, model, previousResponseId, toolCall, usage)),
            cancellationToken);
    }

    private static async Task DrainDeltasAsync(
        HttpContext httpContext,
        PendingRequest pending,
        long created,
        string model,
        CancellationToken cancellationToken)
    {
        while (pending.DeltaReader.TryRead(out var delta))
        {
            await WriteSseAsync(httpContext, CreateTextDeltaChunk(pending.RequestId, created, model, delta), cancellationToken);
        }
    }

    private static async Task WriteSseAsync(HttpContext httpContext, object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        await httpContext.Response.WriteAsync($"data: {json}\n\n", cancellationToken);
        await httpContext.Response.Body.FlushAsync(cancellationToken);
    }

    private static async Task WriteResponsesSseAsync(
        HttpContext httpContext,
        string eventType,
        object payload,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        await httpContext.Response.WriteAsync($"event: {eventType}\ndata: {json}\n\n", cancellationToken);
        await httpContext.Response.Body.FlushAsync(cancellationToken);
    }

    private static object CreateCompletionResponse(
        string requestId,
        long created,
        string model,
        string text,
        TokenUsage usage) => new
    {
        id = $"chatcmpl-{requestId}",
        @object = "chat.completion",
        created,
        model,
        choices = new[]
        {
            new
            {
                index = 0,
                message = new { role = "assistant", content = text },
                finish_reason = "stop",
            },
        },
        usage = CreateChatCompletionUsage(usage),
    };

    private static object CreateToolCompletionResponse(
        string requestId,
        long created,
        string model,
        ToolCallItem toolCall,
        TokenUsage usage)
    {
        var function = (FunctionCallItem)toolCall;
        return new
        {
            id = $"chatcmpl-{requestId}",
            @object = "chat.completion",
            created,
            model,
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new
                    {
                        role = "assistant",
                        content = (string?)null,
                        tool_calls = new[]
                        {
                            new
                            {
                                id = function.CallId,
                                type = "function",
                                function = new { name = function.Name, arguments = function.Arguments },
                            },
                        },
                    },
                    finish_reason = "tool_calls",
                },
            },
            usage = CreateChatCompletionUsage(usage),
        };
    }

    private static object CreateResponseCompletion(
        string requestId,
        long created,
        string model,
        string? previousResponseId,
        string text,
        TokenUsage usage)
        => CreateResponse(
            requestId,
            created,
            model,
            previousResponseId,
            "completed",
            [CreateResponseMessage($"msg_{requestId}", text, "completed")],
            usage);

    private static object CreateResponseToolCompletion(
        string requestId,
        long created,
        string model,
        string? previousResponseId,
        ToolCallItem toolCall,
        TokenUsage usage)
    {
        var function = (FunctionCallItem)toolCall;
        return CreateResponse(
            requestId,
            created,
            model,
            previousResponseId,
            "completed",
            [CreateResponseFunctionCall($"fc_{requestId}", function, "completed")],
            usage);
    }

    private static object CreateResponseInProgress(
        string requestId,
        long created,
        string model,
        string? previousResponseId)
        => new
        {
            type = "response.created",
            sequence_number = 0,
            response = CreateResponse(requestId, created, model, previousResponseId, "in_progress", [], TokenUsage.Empty),
        };

    private static object CreateResponseFailed(
        string requestId,
        long created,
        string model,
        string? previousResponseId,
        string message)
        => new
        {
            type = "response.failed",
            sequence_number = 1,
            response = new
            {
                id = $"resp_{requestId}",
                @object = "response",
                created_at = created,
                status = "failed",
                error = new { code = "timeout", message },
                incomplete_details = (object?)null,
                model,
                output = Array.Empty<object>(),
                previous_response_id = previousResponseId,
                usage = CreateResponseUsage(TokenUsage.Empty),
            },
        };

    private static object CreateResponseCompleted(object response)
        => new
        {
            type = "response.completed",
            sequence_number = 1,
            response,
        };

    private static object CreateResponse(
        string requestId,
        long created,
        string model,
        string? previousResponseId,
        string status,
        IReadOnlyList<object> output,
        TokenUsage usage)
        => new
        {
            id = $"resp_{requestId}",
            @object = "response",
            created_at = created,
            status,
            error = (object?)null,
            incomplete_details = (object?)null,
            instructions = (string?)null,
            model,
            output,
            parallel_tool_calls = true,
            previous_response_id = previousResponseId,
            reasoning = new { effort = (string?)null, summary = (string?)null },
            store = true,
            temperature = 1.0,
            text = new { format = new { type = "text" } },
            tool_choice = "auto",
            tools = Array.Empty<object>(),
            top_p = 1.0,
            truncation = "disabled",
            usage = CreateResponseUsage(usage),
        };

    private static object CreateResponseMessage(string messageId, string text, string status)
        => new
        {
            id = messageId,
            type = "message",
            status,
            role = "assistant",
            content = new[] { CreateResponseTextPart(text) },
        };

    private static object CreateResponseTextPart(string text)
        => new
        {
            type = "output_text",
            text,
            annotations = Array.Empty<object>(),
            logprobs = Array.Empty<object>(),
        };

    private static object CreateResponseFunctionCall(string functionId, FunctionCallItem function, string status)
        => new
        {
            id = functionId,
            type = "function_call",
            status,
            call_id = function.CallId,
            name = function.Name,
            arguments = function.Arguments,
        };

    private static object CreateChatCompletionUsage(TokenUsage usage)
        => new
        {
            prompt_tokens = usage.InputTokens,
            completion_tokens = usage.OutputTokens,
            total_tokens = usage.TotalTokens,
        };

    private static object CreateResponseUsage(TokenUsage usage)
        => new
        {
            input_tokens = usage.InputTokens,
            input_tokens_details = new { cached_tokens = 0 },
            output_tokens = usage.OutputTokens,
            output_tokens_details = new { reasoning_tokens = 0 },
            total_tokens = usage.TotalTokens,
        };

    private static object CreateRoleChunk(string requestId, long created, string model) => new
    {
        id = $"chatcmpl-{requestId}",
        @object = "chat.completion.chunk",
        created,
        model,
        choices = new[] { new { index = 0, delta = new { role = "assistant" }, finish_reason = (string?)null } },
    };

    private static object CreateTextDeltaChunk(string requestId, long created, string model, string delta) => new
    {
        id = $"chatcmpl-{requestId}",
        @object = "chat.completion.chunk",
        created,
        model,
        choices = new[] { new { index = 0, delta = new { content = delta }, finish_reason = (string?)null } },
    };

    private static object CreateToolDeltaChunk(string requestId, long created, string model, ToolCallItem toolCall)
    {
        var function = (FunctionCallItem)toolCall;
        return new
        {
            id = $"chatcmpl-{requestId}",
            @object = "chat.completion.chunk",
            created,
            model,
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta = new
                    {
                        tool_calls = new[]
                        {
                            new
                            {
                                index = 0,
                                id = function.CallId,
                                type = "function",
                                function = new { name = function.Name, arguments = function.Arguments },
                            },
                        },
                    },
                    finish_reason = (string?)null,
                },
            },
        };
    }

    private static object CreateFinishChunk(string requestId, long created, string model, string finishReason) => new
    {
        id = $"chatcmpl-{requestId}",
        @object = "chat.completion.chunk",
        created,
        model,
        choices = new[] { new { index = 0, delta = new { }, finish_reason = finishReason } },
    };

    private static object CreateUsageChunk(string requestId, long created, string model, TokenUsage usage) => new
    {
        id = $"chatcmpl-{requestId}",
        @object = "chat.completion.chunk",
        created,
        model,
        choices = Array.Empty<object>(),
        usage = CreateChatCompletionUsage(usage),
    };

    private sealed record PendingRequestCreation(PendingRequest? Pending, IResult? Error);
}
