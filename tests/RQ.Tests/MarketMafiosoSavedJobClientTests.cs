using RQ.Interop;

namespace RQ.Tests;

public sealed class MarketMafiosoSavedJobClientTests
{
    [Theory]
    [InlineData(true, "Market run (2)")]
    [InlineData(false, null)]
    public void ParsesAcknowledgementAndFinalName(bool accepted, string? name)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new {
            Schema = MarketMafiosoSavedJobClient.Schema, Accepted = accepted, Message = "Result", Name = name });
        var result = MarketMafiosoSavedJobClient.Parse(json);
        Assert.Equal(accepted, result.Accepted);
        Assert.Equal("Result", result.Message);
        Assert.Equal(name, result.Name);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"Schema\":\"wrong\",\"Accepted\":true,\"Message\":\"OK\",\"Name\":null}")]
    [InlineData("{\"Schema\":\"gooseworks-workshop-saved-job-import-response/v1\",\"Accepted\":\"true\",\"Message\":\"OK\",\"Name\":null}")]
    public void RejectsMalformedOrUnsupportedResponses(string json)
    {
        var result = MarketMafiosoSavedJobClient.Parse(json);
        Assert.False(result.Accepted);
        Assert.Equal("Invalid response from MarketMafioso.", result.Message);
    }

    [Fact]
    public void SendsExactJsonAndReportsUnavailableGate()
    {
        string? received = null;
        var client = new MarketMafiosoSavedJobClient(json => { received = json; throw new FakeIpcNotReadyException(); });
        var result = client.Send("{job}");
        Assert.Equal("{job}", received);
        Assert.False(result.Accepted);
        Assert.Equal("MarketMafioso is not available.", result.Message);
    }

    [Fact]
    public void UnexpectedProviderFailureDoesNotEscapeDraw()
    {
        var client = new MarketMafiosoSavedJobClient(_ => throw new InvalidOperationException());
        Assert.Equal("MarketMafioso import failed.", client.Send("{}").Message);
    }

    private sealed class FakeIpcNotReadyException : Exception;
}
