using System.Runtime.CompilerServices;
using System.Threading.Channels;
using PurpleGlass.Adapters.Speech.OpenAI;
using PurpleGlass.Modules.Conversation.Application;

namespace PurpleGlass.UnitTests;

public sealed class OpenAiStreamingSpeechTests
{
    private static readonly RuntimeInvocationContext Context = new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "stream-test");

    [Fact]
    public async Task FirstProviderDeltaIsExposedBeforeProviderCompletion()
    {
        var gateway = new ChannelGateway();
        var synthesizer = Create(gateway);
        await using IAsyncEnumerator<SpeechSynthesisStreamUpdate> stream =
            synthesizer.SynthesizeStreamingAsync(Request(), default).GetAsyncEnumerator();

        Task<bool> firstMove = stream.MoveNextAsync().AsTask();
        gateway.Write(new OpenAiSpeechStreamUpdate(new byte[] { 1, 0 }, Completed: false));

        Assert.True(await firstMove.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(new byte[] { 1, 0 }, stream.Current.AudioChunk?.Audio.ToArray());
        Assert.Null(stream.Current.Completion);
        Assert.False(gateway.Completed);

        gateway.Write(new OpenAiSpeechStreamUpdate(ReadOnlyMemory<byte>.Empty, Completed: true, 4, 6));
        gateway.Complete();
        List<SpeechSynthesisStreamUpdate> remainder = await ReadRemainingAsync(stream);
        Assert.Collection(remainder,
            finalAudio => Assert.True(finalAudio.AudioChunk?.IsFinal),
            completion =>
            {
                Assert.Null(completion.Completion?.Failure);
                Assert.Equal("4", completion.Completion?.Metadata["input-tokens"]);
                Assert.Equal("6", completion.Completion?.Metadata["output-tokens"]);
            });
    }

    [Fact]
    public async Task ArbitraryProviderChunkBoundariesPreserveBytesAndSequence()
    {
        var gateway = new ChannelGateway();
        gateway.Write(new OpenAiSpeechStreamUpdate(new byte[] { 1 }, Completed: false));
        gateway.Write(new OpenAiSpeechStreamUpdate(new byte[] { 2, 3, 4 }, Completed: false));
        gateway.Write(new OpenAiSpeechStreamUpdate(ReadOnlyMemory<byte>.Empty, Completed: true));
        gateway.Complete();

        List<SpeechSynthesisStreamUpdate> updates = await ReadAllAsync(Create(gateway), default);

        Assert.Equal(new long[] { 1, 2, 3 }, updates.Where(x => x.AudioChunk is not null)
            .Select(x => x.AudioChunk!.Sequence));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, updates.Where(x => x.AudioChunk is { IsFinal: false })
            .SelectMany(x => x.AudioChunk!.Audio.ToArray()));
        _ = Assert.Single(updates, x => x.AudioChunk?.IsFinal == true);
        _ = Assert.Single(updates, x => x.Completion is not null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NetworkFailureBeforeOrAfterAudioProducesSafeFailureCompletion(bool emitAudio)
    {
        static async IAsyncEnumerable<OpenAiSpeechStreamUpdate> Updates(
            bool emitAudio,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (emitAudio)
                yield return new OpenAiSpeechStreamUpdate(new byte[] { 1, 0 }, Completed: false);
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            throw new HttpRequestException("secret provider response");
        }
        var gateway = new DelegateGateway(token => Updates(emitAudio, token));

        List<SpeechSynthesisStreamUpdate> updates = await ReadAllAsync(Create(gateway), default);

        Assert.Equal(emitAudio ? 1 : 0, updates.Count(x => x.AudioChunk is not null));
        SpeechSynthesisResult completion = Assert.Single(updates, x => x.Completion is not null).Completion!;
        Assert.Equal("speech_synthesis_network_failed", completion.Failure?.Code);
        Assert.DoesNotContain("secret", completion.Failure?.SafeMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancellationBetweenDeltasStopsProviderEnumeration()
    {
        var gateway = new ChannelGateway();
        var synthesizer = Create(gateway);
        using var cancellation = new CancellationTokenSource();
        await using IAsyncEnumerator<SpeechSynthesisStreamUpdate> stream =
            synthesizer.SynthesizeStreamingAsync(Request(), cancellation.Token).GetAsyncEnumerator();
        gateway.Write(new OpenAiSpeechStreamUpdate(new byte[] { 0, 0 }, Completed: false));
        Assert.True(await stream.MoveNextAsync());

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream.MoveNextAsync().AsTask());
    }

    private static OpenAiSpeechSynthesizer Create(IOpenAiSpeechStreamingGateway gateway) => new(
        new HttpClient(),
        new OpenAiSpeechOptions
        {
            ApiKey = "unit-test-key",
            BaseUri = new Uri("https://unit.openai.test/v1/"),
            TranscriptionModel = "gpt-4o-mini-transcribe",
            SynthesisModel = "gpt-4o-mini-tts",
        },
        gateway);

    private static SpeechSynthesisRequest Request() => new(
        Context, "A helpful response.", "en-US", new VoiceConfiguration("alloy", "calm", 1m));

    private static async Task<List<SpeechSynthesisStreamUpdate>> ReadAllAsync(
        OpenAiSpeechSynthesizer synthesizer,
        CancellationToken cancellationToken)
    {
        var result = new List<SpeechSynthesisStreamUpdate>();
        await foreach (SpeechSynthesisStreamUpdate update in synthesizer.SynthesizeStreamingAsync(
            Request(), cancellationToken)) result.Add(update);
        return result;
    }

    private static async Task<List<SpeechSynthesisStreamUpdate>> ReadRemainingAsync(
        IAsyncEnumerator<SpeechSynthesisStreamUpdate> stream)
    {
        var result = new List<SpeechSynthesisStreamUpdate>();
        while (await stream.MoveNextAsync()) result.Add(stream.Current);
        return result;
    }

    private sealed class ChannelGateway : IOpenAiSpeechStreamingGateway
    {
        private readonly Channel<OpenAiSpeechStreamUpdate> updates = Channel.CreateUnbounded<OpenAiSpeechStreamUpdate>();
        public bool Completed { get; private set; }
        public void Write(OpenAiSpeechStreamUpdate update) => Assert.True(updates.Writer.TryWrite(update));
        public void Complete()
        {
            Completed = true;
            updates.Writer.Complete();
        }

        public IAsyncEnumerable<OpenAiSpeechStreamUpdate> GenerateAsync(
            string text, string voice, string? instructions, decimal speakingRate,
            CancellationToken cancellationToken) => updates.Reader.ReadAllAsync(cancellationToken);
    }

    private sealed class DelegateGateway(
        Func<CancellationToken, IAsyncEnumerable<OpenAiSpeechStreamUpdate>> updates)
        : IOpenAiSpeechStreamingGateway
    {
        public IAsyncEnumerable<OpenAiSpeechStreamUpdate> GenerateAsync(
            string text, string voice, string? instructions, decimal speakingRate,
            CancellationToken cancellationToken) => updates(cancellationToken);
    }
}
