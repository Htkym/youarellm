using System.Diagnostics;
using System.Text.Json;
using YouAreLlm.Core;

namespace YouAreLlm.Web.Api;

internal static class CopilotRequestTelemetry
{
    internal const string ActivitySourceName = "YouAreLlm.OpenAI";

    private static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    internal static Activity? Start(
        string protocol,
        string model,
        bool stream,
        int inputMessageCount,
        JsonElement? tools)
    {
        var activity = ActivitySource.StartActivity("gen_ai.chat", ActivityKind.Internal);
        activity?.SetTag("gen_ai.operation.name", "chat");
        activity?.SetTag("gen_ai.provider.name", "openai");
        activity?.SetTag("gen_ai.request.model", model);
        activity?.SetTag("gen_ai.request.stream", stream);
        activity?.SetTag("youarellm.protocol", protocol);
        activity?.SetTag("youarellm.input.message_count", inputMessageCount);
        activity?.SetTag("youarellm.request.has_tools", HasDeclaredTools(tools));
        return activity;
    }

    internal static void Complete(Activity? activity, HumanCompletion completion)
    {
        activity?.SetTag(
            "youarellm.completion.kind",
            completion is ToolCompletion ? "tool_call" : "text");
        activity?.SetStatus(ActivityStatusCode.Ok);
    }

    internal static void Fail(Activity? activity, string errorType)
    {
        activity?.SetTag("error.type", errorType);
        activity?.SetStatus(ActivityStatusCode.Error);
    }

    private static bool HasDeclaredTools(JsonElement? tools)
        => tools is { ValueKind: JsonValueKind.Array } array
            ? array.GetArrayLength() > 0
            : tools is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined };
}
