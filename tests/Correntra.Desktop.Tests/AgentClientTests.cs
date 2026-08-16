using System.IO.Pipes;
using System.Text.Json;
using Correntra.Core;
using Correntra.Core.Downloads;
using Correntra.Core.Ipc;
using Correntra.Desktop.Services;
using Correntra.Infrastructure.Ipc;
using Xunit;

namespace Correntra.Desktop.Tests;

public sealed class AgentClientTests
{
    [Fact]
    public async Task SnapshotRoundTripsAcrossTheDesktopPipeContract()
    {
        string pipeName = "Correntra.Tests." + Guid.NewGuid().ToString("N");
        var protocol = new LengthPrefixedJsonProtocol();
        JobId jobId = JobId.Create();
        Task server = Task.Run(async () =>
        {
            await using var pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            await pipe.WaitForConnectionAsync();
            using JsonDocument? request = await protocol.ReadAsync<JsonDocument>(pipe);
            Assert.NotNull(request);
            string requestId = request.RootElement.GetProperty("requestId").GetString()!;
            var snapshot = new AgentSnapshot(
                DateTimeOffset.UtcNow,
                [
                    new DownloadJobSnapshot(
                        jobId,
                        1,
                        "archive.zip",
                        Path.GetTempPath(),
                        "https://example.test/archive.zip",
                        DownloadJobState.Downloading,
                        512,
                        1024,
                        DateTimeOffset.UtcNow),
                ],
                aggregateBytesPerSecond: 256);
            await protocol.WriteAsync(pipe, new
            {
                protocolVersion = 1,
                kind = "response",
                requestId,
                timestampUtc = DateTimeOffset.UtcNow,
                payload = new
                {
                    accepted = true,
                    reason = (string?)null,
                    hostVersion = "0.1.0",
                    jobId = (string?)null,
                    snapshot,
                },
            });
        });

        var client = new AgentClient(pipeName);
        AgentCommandResult result = await client.GetSnapshotAsync();
        await server;

        Assert.True(result.Accepted);
        Assert.NotNull(result.Snapshot);
        DownloadJobSnapshot job = Assert.Single(result.Snapshot.Jobs);
        Assert.Equal(jobId, job.Id);
        Assert.Equal(256, result.Snapshot.AggregateBytesPerSecond);
    }
}
