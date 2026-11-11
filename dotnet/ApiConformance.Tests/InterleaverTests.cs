using ApiConformance;

public class InterleaverTests : IAsyncLifetime
{
    private ApiHost host = null!;

    public Task InitializeAsync()
    {
        host = GeneratedApp.Compile(StepsApp.Source).Host();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => host.DisposeAsync().AsTask();

    private Task<HttpTrace[]> WhenWalkingConcurrently(TimeSpan timeout) => WhenRunning(timeout, "a", "b");

    private Task<HttpTrace[]> WhenRunning(TimeSpan timeout, params string[] tags) =>
        host.Interleaver.Run<HttpTrace>([.. tags.Select<string, Func<string, Task<HttpTrace>>>(tag => id =>
            HttpTrace.Send(host.Client, HttpMethod.Get, $"/api/steps/{tag}", null, new Dictionary<string, string> { [Interleaver.ParticipantHeader] = id }))], timeout);

    private async Task<string[]> WhenReadingTheAppLog()
    {
        var trace = await HttpTrace.Send(host.Client, HttpMethod.Get, "/api/steps/log");
        return System.Text.Json.JsonSerializer.Deserialize<string[]>(trace.ResponseBody)!;
    }

    [Fact]
    public async Task GivenTwoParticipants_WhenEachRunsFourCommands_ThenCommandsAdvanceInLockstep()
    {
        var responses = await WhenWalkingConcurrently(TimeSpan.FromSeconds(20));

        Assert.All(responses, trace => Assert.Equal(200, trace.StatusCode));
        var steps = (await WhenReadingTheAppLog()).Select(entry => int.Parse(entry[1..])).ToArray();
        Assert.Equal([1, 1, 2, 2, 3, 3, 4, 4], steps);
    }

    [Fact]
    public async Task GivenTwoParticipantsWriting_WhenSecondReachesTheWrite_ThenItIsBlockedUntilTheFirstWriteEnds()
    {
        await WhenWalkingConcurrently(TimeSpan.FromSeconds(20));

        var events = host.Interleaver.Events.ToList();
        var blocked = events.FindIndex(e => e.StartsWith("1 blocked by the write lock of 0"));
        var firstDone = events.FindIndex(e => e == "0 done");
        var second = events.FindIndex(e => e.StartsWith("1 runs [INSERT"));
        Assert.True(0 <= blocked && blocked < firstDone && firstDone < second, string.Join(Environment.NewLine, events));
    }

    [Fact]
    public async Task GivenReaderAndMultiStatementTransaction_WhenInterleaved_ThenNeitherBlocksTheOther()
    {
        await HttpTrace.Send(host.Client, HttpMethod.Get, "/api/steps/seed");

        var traces = await host.Interleaver.Run<HttpTrace>([
            id => HttpTrace.Send(host.Client, HttpMethod.Get, "/api/steps/tx/w", null, new Dictionary<string, string> { [Interleaver.ParticipantHeader] = id }),
            id => HttpTrace.Send(host.Client, HttpMethod.Get, "/api/steps/r", null, new Dictionary<string, string> { [Interleaver.ParticipantHeader] = id })], TimeSpan.FromSeconds(5));

        Assert.All(traces, trace => Assert.Equal(200, trace.StatusCode));
    }

    [Fact]
    public async Task GivenNoArmedParticipants_WhenRequestsRun_ThenNothingIsGated()
    {
        var trace = await HttpTrace.Send(host.Client, HttpMethod.Get, "/api/steps/solo");

        Assert.Equal(200, trace.StatusCode);
        Assert.Empty(host.Interleaver.Events);
    }

    [Fact]
    public async Task GivenParticipantThatNeverFinishes_WhenTimeoutElapses_ThenScenarioFailsWithState()
    {
        var failure = await Assert.ThrowsAsync<ScenarioFailure>(() => WhenRunning(TimeSpan.FromSeconds(1), "hang", "a"));

        Assert.Contains("did not finish within 1s under lockstep", failure.Message);
        Assert.Contains("0=Running, 1=Running", failure.Message);
    }

    [SoakFact]
    [Trait("Category", "Soak")]
    public async Task GivenReaderAndMultiStatementTransaction_WhenInterleavedSixtyTimes_ThenNeverHangs()
    {
        for (var run = 0; run < 60; run++)
        {
            await using var fresh = GeneratedApp.Compile(StepsApp.Source).Host();
            await HttpTrace.Send(fresh.Client, HttpMethod.Get, "/api/steps/seed");

            var traces = await fresh.Interleaver.Run<HttpTrace>([
                id => HttpTrace.Send(fresh.Client, HttpMethod.Get, "/api/steps/tx/w", null, new Dictionary<string, string> { [Interleaver.ParticipantHeader] = id }),
                id => HttpTrace.Send(fresh.Client, HttpMethod.Get, "/api/steps/r", null, new Dictionary<string, string> { [Interleaver.ParticipantHeader] = id })], TimeSpan.FromSeconds(5));

            Assert.All(traces, trace => Assert.Equal(200, trace.StatusCode));
        }
    }
}
