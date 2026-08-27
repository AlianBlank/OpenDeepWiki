using System.Text.Json;
using OpenDeepWiki.Agents;
using Xunit;

namespace OpenDeepWiki.Tests.Agents;

/// <summary>
/// Unit tests for <see cref="ServerToolUsageNormalizingHandler"/>.
/// Exercises the internal static helper directly so no HTTP stack is required.
/// </summary>
public class ServerToolUsageNormalizingHandlerTests
{
    // ------------------------------------------------------------------
    // TransformLine - missing field completion
    // ------------------------------------------------------------------

    [Fact]
    public void TransformLine_MissingWebFetchRequests_IsAppendedAsZero()
    {
        const string line = "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"usage\":{\"input_tokens\":25,\"output_tokens\":1,\"server_tool_usage\":{\"web_search_requests\":0}}}}";
        var result = ServerToolUsageNormalizingHandler.TransformLine(line);
        Assert.Contains("\"server_tool_usage\":{\"web_search_requests\":0,\"web_fetch_requests\":0}", result);
    }

    [Fact]
    public void TransformLine_MissingWebSearchRequests_IsAppendedAsZero()
    {
        const string line = "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":123,\"server_tool_usage\":{\"web_fetch_requests\":2}}}";
        var result = ServerToolUsageNormalizingHandler.TransformLine(line);
        Assert.Contains("\"web_fetch_requests\":2,\"web_search_requests\":0", result);
    }

    [Fact]
    public void TransformLine_EmptyObject_GetsBothFields()
    {
        const string line = "data: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":25,\"server_tool_usage\":{}}}}";
        var result = ServerToolUsageNormalizingHandler.TransformLine(line);
        Assert.Contains("\"server_tool_usage\":{\"web_search_requests\":0,\"web_fetch_requests\":0}", result);
    }

    [Fact]
    public void TransformLine_ExistingValues_AreNotModified()
    {
        const string line = "data: {\"usage\":{\"server_tool_usage\":{\"web_search_requests\":5,\"web_fetch_requests\":7}}}";
        var result = ServerToolUsageNormalizingHandler.TransformLine(line);
        Assert.Equal(line, result);
    }

    [Fact]
    public void TransformLine_PatchedJson_RemainsParsableWithCorrectValues()
    {
        const string line = "data: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":25,\"server_tool_usage\":{\"web_search_requests\":3}}}}";
        var result = ServerToolUsageNormalizingHandler.TransformLine(line);
        using (var doc = JsonDocument.Parse(result.Substring("data: ".Length)))
        {
            var usage = doc.RootElement.GetProperty("message").GetProperty("usage").GetProperty("server_tool_usage");
            Assert.Equal(3, usage.GetProperty("web_search_requests").GetInt32());
            Assert.Equal(0, usage.GetProperty("web_fetch_requests").GetInt32());
        }
    }

    // ------------------------------------------------------------------
    // TransformLine - gating (non-matching lines pass through)
    // ------------------------------------------------------------------

    [Fact]
    public void TransformLine_NonDataLine_IsUnchanged()
    {
        const string line = "event: message_start";
        var result = ServerToolUsageNormalizingHandler.TransformLine(line);
        Assert.Equal(line, result);
    }

    [Fact]
    public void TransformLine_OpenAiProtocolLine_IsUnchanged()
    {
        const string line = "data: {\"id\":\"chatcmpl-1\",\"choices\":[{\"finish_reason\":\"stop\",\"delta\":{}}],\"usage\":{\"prompt_tokens\":10}}";
        var result = ServerToolUsageNormalizingHandler.TransformLine(line);
        Assert.Equal(line, result);
    }
}
