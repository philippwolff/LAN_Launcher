using System.Net;
using System.Net.Http;

namespace LanLauncher.Client;

public sealed class ProgressStreamContent : HttpContent
{
    private readonly Stream input;
    private readonly long length;
    private readonly Action<long, long> progress;
    private readonly CancellationToken cancellation;

    public ProgressStreamContent(Stream input, Action<long, long> progress, CancellationToken cancellation)
    {
        this.input = input;
        length = input.Length;
        this.progress = progress;
        this.cancellation = cancellation;
        Headers.ContentLength = length;
    }

    protected override bool TryComputeLength(out long result) { result = length; return true; }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        await CopyAsync(stream, cancellation);
    }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, cancellationToken);
        await CopyAsync(stream, linked.Token);
    }

    private async Task CopyAsync(Stream output, CancellationToken token)
    {
        var buffer = new byte[128 * 1024];
        long sent = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, token)) != 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, count), token);
            sent += count;
            progress(sent, length);
        }
    }
}
