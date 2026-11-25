using System.IO.Pipes;
using Microsoft.Extensions.Logging.Abstractions;
using Valenius.Service;
using Xunit;

namespace Valenius.Service.Tests;

public sealed class IpcLimitTests
{
    [Fact]
    public async Task Partial_frame_expires_without_dispatch()
    {
        var name = "Valenius-limits-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var accept = server.WaitForConnectionAsync();
        await client.ConnectAsync(2000);
        await accept;
        var read = PipeServer.ReadMessageAsync(server, CancellationToken.None);
        await client.WriteAsync(new byte[] { 10, 0 });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
    }

    [Fact]
    public async Task Oversized_response_is_rejected_before_writing()
    {
        await using var pipe = new NamedPipeServerStream("Valenius-limits-" + Guid.NewGuid().ToString("N"), PipeDirection.InOut);
        await Assert.ThrowsAsync<InvalidDataException>(() => PipeServer.WriteMessageAsync(pipe, new string('x', 1_048_577), CancellationToken.None));
    }

    [Fact]
    public async Task Download_copy_accepts_boundary_and_rejects_extra_bytes()
    {
        using var output = new MemoryStream();
        await UpdateChecker.CopyBoundedAsync(new MemoryStream(new byte[8]), output, 8, CancellationToken.None);
        Assert.Equal(8, output.Length);
        output.SetLength(0);
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateChecker.CopyBoundedAsync(new MemoryStream(new byte[9]), output, 8, CancellationToken.None));
        Assert.True(output.Length <= 8);
    }
}
