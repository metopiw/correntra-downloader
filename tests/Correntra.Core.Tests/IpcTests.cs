using Correntra.Core.Downloads;
using Correntra.Core.Ipc;
using Correntra.Core.Security;

namespace Correntra.Core.Tests;

public sealed class IpcTests
{
    [Fact]
    public void EnvelopeValidatesKindVersionIdAndUtcTimestamp()
    {
        PingCommand command = new();
        IpcEnvelope<PingCommand> envelope = IpcEnvelope.Create(command, TestData.Timestamp);

        Assert.Equal(IpcProtocol.CurrentVersion, envelope.ProtocolVersion);
        Assert.Equal(IpcMessageKind.Command, envelope.Kind);
        Assert.Equal("ping", envelope.Payload.Type);

        Assert.Throws<ArgumentException>(() => new IpcEnvelope<PingCommand>(
            1,
            IpcMessageKind.Response,
            IpcRequestId.Create(),
            TestData.Timestamp,
            command));
        Assert.Throws<ArgumentException>(() => new IpcEnvelope<PingCommand>(
            1,
            IpcMessageKind.Command,
            IpcRequestId.Create(),
            TestData.Timestamp.ToOffset(TimeSpan.FromHours(3)),
            command));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(IpcProtocol.MaximumFrameLength + 1)]
    public void RejectsInvalidFrameLengths(long length)
    {
        Assert.Throws<InvalidDataException>(() => IpcProtocol.ValidateFrameLength(length));
    }

    [Fact]
    public void AllowsMaximumFrameLength()
    {
        IpcProtocol.ValidateFrameLength(IpcProtocol.MaximumFrameLength);
    }

    [Theory]
    [InlineData("ping", true)]
    [InlineData("browser.download.capture", true)]
    [InlineData("media.candidate.select", true)]
    [InlineData("settings.update", false)]
    [InlineData("download.remove", false)]
    [InlineData(null, false)]
    public void IpcCommandAllowListIsStrict(string? commandType, bool expected)
    {
        Assert.Equal(expected, IpcCommandTypes.IsAllowed(commandType));
    }

    [Fact]
    public void SnapshotRedactsSignedSourceUrl()
    {
        HttpHeaderSet headers = new([new("Cookie", "sid=secret")]);
        DownloadJob job = DownloadJob.Create(
            TestData.Source("https://cdn.example.test/file.bin?token=secret&expires=1", headers),
            "file.bin",
            TestData.DestinationDirectory,
            TestData.Timestamp);

        DownloadJobSnapshot snapshot = DownloadJobSnapshot.FromJob(job);

        Assert.DoesNotContain("secret", snapshot.SourceDisplayUri, StringComparison.Ordinal);
        Assert.DoesNotContain("token", snapshot.SourceDisplayUri, StringComparison.Ordinal);
        Assert.DoesNotContain("Cookie", snapshot.SourceDisplayUri, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentSnapshotMaterializesInputCollections()
    {
        List<DownloadJobSnapshot> jobs =
        [
            DownloadJobSnapshot.FromJob(TestData.PendingJob()),
        ];
        AgentSnapshot snapshot = new(TestData.Timestamp, jobs);
        jobs.Clear();

        Assert.Single(snapshot.Jobs);
        Assert.Equal("agent.snapshot", snapshot.Type);
    }
}
