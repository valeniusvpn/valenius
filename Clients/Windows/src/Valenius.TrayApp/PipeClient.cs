using System.IO.Pipes;
using System.Text.Json;
using Valenius.Shared;

namespace Valenius.TrayApp;

public class PipeClient
{
    private const string PipeName = "Valenius";
    private const int TimeoutMs = 5000;

    public async Task<PipeResponse> SendAsync(PipeCommand command)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(TimeoutMs);

        var json = JsonSerializer.Serialize(command, ValeniusJsonContext.Default.PipeCommand);
        await WriteMessageAsync(pipe, json);

        var responseJson = await ReadMessageAsync(pipe);
        return JsonSerializer.Deserialize(responseJson, ValeniusJsonContext.Default.PipeResponse)
               ?? new PipeResponse { Success = false, Error = "Empty response from service." };
    }

    private static async Task WriteMessageAsync(PipeStream pipe, string message)
    {
        var data = System.Text.Encoding.UTF8.GetBytes(message);
        var lenBuf = BitConverter.GetBytes(data.Length);
        await pipe.WriteAsync(lenBuf);
        await pipe.WriteAsync(data);
        await pipe.FlushAsync();
    }

    private static async Task<string> ReadMessageAsync(PipeStream pipe)
    {
        var lenBuf = new byte[4];
        await pipe.ReadExactlyAsync(lenBuf);
        var length = BitConverter.ToInt32(lenBuf, 0);
        var data = new byte[length];
        await pipe.ReadExactlyAsync(data);
        return System.Text.Encoding.UTF8.GetString(data);
    }
}
