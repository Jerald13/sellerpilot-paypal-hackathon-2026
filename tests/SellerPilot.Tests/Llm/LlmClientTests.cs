using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SellerPilot.Api.Agent.Tools;
using SellerPilot.Api.Configuration;
using SellerPilot.Api.Llm;
using SellerPilot.Tests.TestSupport;

namespace SellerPilot.Tests.Llm;

public class ClaudeLlmClientTests
{
    private static readonly LlmToolDefinition GetOrder = new(
        "GetOrder",
        "Look up an order.",
        ToolSchema.Parse("""{"type":"object","properties":{"order_id":{"type":"string","description":"Order ID"}},"required":["order_id"]}"""));

    private const string ToolUseResponse = """
        {
          "id": "msg_01",
          "type": "message",
          "role": "assistant",
          "model": "claude-opus-5-5",
          "content": [
            { "type": "thinking", "thinking": "", "signature": "sig-thinking-1" },
            { "type": "tool_use", "id": "toolu_01", "name": "GetOrder", "input": { "order_id": "5O190127TN364715T" } }
          ],
          "stop_reason": "tool_use",
          "stop_sequence": null,
          "usage": { "input_tokens": 1500, "output_tokens": 60, "cache_creation_input_tokens": 1400, "cache_read_input_tokens": 0 }
        }
        """;

    private const string TextResponse = """
        {
          "id": "msg_02",
          "type": "message",
          "role": "assistant",
          "model": "claude-opus-5-5",
          "content": [ { "type": "text", "text": "Order 5O190127TN364715T was paid on 2026-10-20." } ],
          "stop_reason": "end_turn",
          "stop_sequence": null,
          "usage": { "input_tokens": 1700, "output_tokens": 30, "cache_creation_input_tokens": 0, "cache_read_input_tokens": 1400 }
        }
        """;

    private readonly FakeHttpMessageHandler _handler = new();

    private ClaudeLlmClient CreateClient(string model = "claude-opus-5-5") => new(
        new ClaudeOptions { ApiKey = "test-key", Model = model, Effort = "low", MaxTokens = 4000 },
        TimeSpan.FromSeconds(30),
        NullLogger<ClaudeLlmClient>.Instance,
        new HttpClient(_handler) { Timeout = TimeSpan.FromSeconds(60) });

    [Fact]
    public async Task Sends_tools_effort_and_fallback_without_sampling_parameters()
    {
        _handler.Respond(HttpStatusCode.OK, ToolUseResponse);

        var response = await CreateClient().CompleteAsync(new LlmRequest("You are SellerPilot.", [LlmMessage.User("Was order 5O190127TN364715T paid?")], [GetOrder]), CancellationToken.None);

        var request = Assert.Single(_handler.Requests);
        Assert.EndsWith("/v1/messages", request.Uri.AbsolutePath);
        // SDK 13.x (API version 2026-10-21) sends the configured API key as a bearer token.
        Assert.Equal("Bearer test-key", request.Header("Authorization"));
        Assert.Contains("server-side-fallback-2026-07-01", request.Header("anthropic-beta"));

        using var body = JsonDocument.Parse(request.Body!);
        var root = body.RootElement;
        Assert.Equal("claude-opus-5-5", root.GetProperty("model").GetString());
        Assert.Equal(4000, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal("You are SellerPilot.", root.GetProperty("system").GetString());
        Assert.Equal("low", root.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Equal("default", root.GetProperty("fallbacks").GetString());
        Assert.Equal("ephemeral", root.GetProperty("cache_control").GetProperty("type").GetString());
        Assert.False(root.TryGetProperty("temperature", out _));
        Assert.False(root.TryGetProperty("thinking", out _));
        var tool = root.GetProperty("tools")[0];
        Assert.Equal("GetOrder", tool.GetProperty("name").GetString());
        Assert.Equal("object", tool.GetProperty("input_schema").GetProperty("type").GetString());
        Assert.Equal("order_id", tool.GetProperty("input_schema").GetProperty("required")[0].GetString());

        Assert.Equal(LlmStopReason.ToolUse, response.StopReason);
        var call = Assert.Single(response.ToolCalls);
        Assert.Equal("toolu_01", call.Id);
        Assert.Equal("5O190127TN364715T", call.Arguments.GetProperty("order_id").GetString());
    }

    [Fact]
    public async Task Replays_thinking_blocks_and_tool_results_unchanged()
    {
        _handler.Respond(HttpStatusCode.OK, ToolUseResponse).Respond(HttpStatusCode.OK, TextResponse);
        var client = CreateClient();
        var messages = new List<LlmMessage> { LlmMessage.User("Was it paid?") };

        var first = await client.CompleteAsync(new LlmRequest("sys", messages, [GetOrder]), CancellationToken.None);
        messages.Add(first.Message);
        messages.Add(LlmMessage.FromToolResults([new LlmToolResult("toolu_01", "GetOrder", """{"ok":true}""", false)]));
        var second = await client.CompleteAsync(new LlmRequest("sys", messages, [GetOrder]), CancellationToken.None);

        Assert.Equal(LlmStopReason.EndTurn, second.StopReason);
        Assert.Equal("Order 5O190127TN364715T was paid on 2026-10-20.", second.Text);
        Assert.Equal(1400, second.Usage!.CacheReadInputTokens);

        using var body = JsonDocument.Parse(_handler.Requests[1].Body!);
        var sent = body.RootElement.GetProperty("messages");
        Assert.Equal(3, sent.GetArrayLength());
        var assistant = sent[1].GetProperty("content");
        Assert.Equal("thinking", assistant[0].GetProperty("type").GetString());
        Assert.Equal("sig-thinking-1", assistant[0].GetProperty("signature").GetString());
        Assert.Equal("tool_use", assistant[1].GetProperty("type").GetString());
        var result = sent[2].GetProperty("content")[0];
        Assert.Equal("tool_result", result.GetProperty("type").GetString());
        Assert.Equal("toolu_01", result.GetProperty("tool_use_id").GetString());
    }

    [Fact]
    public async Task Refusal_is_mapped_with_its_category()
    {
        _handler.Respond(HttpStatusCode.OK, """
            {"id":"msg_03","type":"message","role":"assistant","model":"claude-opus-5-5","content":[],
             "stop_reason":"refusal","stop_sequence":null,
             "stop_details":{"type":"refusal","category":"cyber","explanation":null},
             "usage":{"input_tokens":10,"output_tokens":0}}
            """);

        var response = await CreateClient().CompleteAsync(new LlmRequest("sys", [LlmMessage.User("x")], [GetOrder]), CancellationToken.None);

        Assert.Equal(LlmStopReason.Refusal, response.StopReason);
        Assert.Equal("cyber", response.RefusalCategory);
    }

    [Fact]
    public async Task Blocks_before_a_fallback_boundary_are_not_replayed_except_text()
    {
        _handler.Respond(HttpStatusCode.OK, """
            {"id":"msg_04","type":"message","role":"assistant","model":"claude-opus-5","content":[
               {"type":"thinking","thinking":"","signature":"sig-declined"},
               {"type":"fallback","from":{"model":"claude-opus-5-5"},"to":{"model":"claude-opus-5"}},
               {"type":"thinking","thinking":"","signature":"sig-served"},
               {"type":"text","text":"Here is the answer."}],
             "stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":5}}
            """);

        var response = await CreateClient().CompleteAsync(new LlmRequest("sys", [LlmMessage.User("x")], [GetOrder]), CancellationToken.None);

        Assert.Equal("Here is the answer.", response.Text);
        var replay = JsonSerializer.Serialize(response.Message.ProviderState);
        Assert.DoesNotContain("sig-declined", replay);
        Assert.Contains("sig-served", replay);
    }

    [Fact]
    public async Task Models_without_server_side_fallback_do_not_send_it()
    {
        _handler.Respond(HttpStatusCode.OK, TextResponse);

        await CreateClient("claude-haiku-5-5").CompleteAsync(new LlmRequest("sys", [LlmMessage.User("x")], [GetOrder]), CancellationToken.None);

        using var body = JsonDocument.Parse(_handler.Requests[0].Body!);
        Assert.False(body.RootElement.TryGetProperty("fallbacks", out _));
    }
}

public class GeminiLlmClientTests
{
    private static readonly LlmToolDefinition GetOrder = new(
        "GetOrder",
        "Look up an order.",
        ToolSchema.Parse("""{"type":"object","properties":{"order_id":{"type":"string"}},"required":["order_id"]}"""));

    private const string FunctionCallResponse = """
        {
          "candidates": [{
            "content": {
              "role": "model",
              "parts": [
                { "functionCall": { "id": "fc-1", "name": "GetOrder", "args": { "order_id": "5O190127TN364715T" } }, "thoughtSignature": "c2lnLTE=" }
              ]
            },
            "finishReason": "STOP"
          }],
          "usageMetadata": { "promptTokenCount": 900, "candidatesTokenCount": 20 },
          "modelVersion": "gemini-3.5-flash"
        }
        """;

    [Fact]
    public void Parses_function_calls_and_keeps_the_raw_content_for_replay()
    {
        var response = GeminiLlmClient.ParseResponse(FunctionCallResponse, "gemini-flash-latest");

        Assert.Equal(LlmStopReason.ToolUse, response.StopReason);
        var call = Assert.Single(response.ToolCalls);
        Assert.Equal("fc-1", call.Id);
        Assert.Equal("GetOrder", call.Name);
        Assert.Equal("gemini-3.5-flash", response.Model);
        Assert.IsType<JsonElement>(response.Message.ProviderState);
    }

    [Fact]
    public void Replays_model_turns_verbatim_including_thought_signatures()
    {
        var first = GeminiLlmClient.ParseResponse(FunctionCallResponse, "m");
        var messages = new List<LlmMessage>
        {
            LlmMessage.User("Was it paid?"),
            first.Message,
            LlmMessage.FromToolResults([new LlmToolResult("fc-1", "GetOrder", """{"ok":true,"data":{"status":"COMPLETED"}}""", false)]),
        };

        var body = GeminiLlmClient.BuildRequestBody(new LlmRequest("sys", messages, [GetOrder]), new GeminiOptions());

        var contents = body["contents"]!.AsArray();
        Assert.Equal("c2lnLTE=", contents[1]!["parts"]![0]!["thoughtSignature"]!.GetValue<string>());
        var functionResponse = contents[2]!["parts"]![0]!["functionResponse"]!;
        Assert.Equal("user", contents[2]!["role"]!.GetValue<string>());
        Assert.Equal("fc-1", functionResponse["id"]!.GetValue<string>());
        Assert.Equal("GetOrder", functionResponse["name"]!.GetValue<string>());
        Assert.True(functionResponse["response"]!["ok"]!.GetValue<bool>());
        Assert.Equal("sys", body["systemInstruction"]!["parts"]![0]!["text"]!.GetValue<string>());
        Assert.Null(body["generationConfig"]!["temperature"]);
        Assert.Equal("GetOrder", body["tools"]![0]!["functionDeclarations"]![0]!["name"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("""{"promptFeedback":{"blockReason":"SAFETY"}}""")]
    [InlineData("""{"candidates":[{"content":{"role":"model","parts":[]},"finishReason":"PROHIBITED_CONTENT"}]}""")]
    public void Safety_blocks_are_refusals(string json) =>
        Assert.Equal(LlmStopReason.Refusal, GeminiLlmClient.ParseResponse(json, "m").StopReason);

    [Fact]
    public void Text_answers_end_the_turn_and_thought_parts_are_hidden()
    {
        var response = GeminiLlmClient.ParseResponse("""
            {"candidates":[{"content":{"role":"model","parts":[{"text":"thinking...","thought":true},{"text":"Two invoices are unpaid."}]},"finishReason":"STOP"}]}
            """, "m");

        Assert.Equal(LlmStopReason.EndTurn, response.StopReason);
        Assert.Equal("Two invoices are unpaid.", response.Text);
    }

    [Fact]
    public void Synthetic_ids_are_not_sent_back()
    {
        var response = GeminiLlmClient.ParseResponse("""
            {"candidates":[{"content":{"role":"model","parts":[{"functionCall":{"name":"GetOrder","args":{"order_id":"X"}}}]},"finishReason":"STOP"}]}
            """, "m");
        var call = Assert.Single(response.ToolCalls);

        var body = GeminiLlmClient.BuildRequestBody(new LlmRequest("sys", [
            LlmMessage.User("q"),
            response.Message,
            LlmMessage.FromToolResults([new LlmToolResult(call.Id, call.Name, "{}", false)]),
        ], [GetOrder]), new GeminiOptions());

        var functionResponse = body["contents"]![2]!["parts"]![0]!["functionResponse"]!.AsObject();
        Assert.False(functionResponse.ContainsKey("id"));
    }
}
