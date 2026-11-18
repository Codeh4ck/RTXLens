using System.Buffers.Binary;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using RTXLens.Contracts.Mobile;

namespace RTXLens.Client.Mobile;

[SupportedOSPlatform("windows")]
public sealed class MobileAccessClient(bool debug)
{
    public async Task<MobileAccessResponse> SendAsync(MobileAccessRequest request, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));

        await using var pipe = new NamedPipeClientStream(".", MobileAccessProtocol.GetPipeName(debug), PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);

        try
        {
            await pipe.ConnectAsync(1500, deadline.Token);
        }
        catch (TimeoutException)
        {
            throw new IOException("The local RTX Lens service does not expose Mobile Access. Check that the updated service is running.");
        }

        RequireTrustedServer(pipe);

        var payload = JsonSerializer.SerializeToUtf8Bytes(request, MobileAccessProtocol.Json);
        var header = new byte[sizeof(int)];

        try
        {
            if (payload.Length > MobileAccessProtocol.MaxRequestSize)
            {
                throw new ArgumentException("The Mobile Access request is too large.");
            }

            BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);

            await pipe.WriteAsync(header, deadline.Token);
            await pipe.WriteAsync(payload, deadline.Token);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }

        await pipe.ReadExactlyAsync(header, deadline.Token);

        var size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size is <= 0 or > MobileAccessProtocol.MaxResponseSize)
        {
            throw new IOException("The RTX Lens service returned an invalid Mobile Access response.");
        }

        var response = new byte[size];
        try
        {
            await pipe.ReadExactlyAsync(response, deadline.Token);
            return JsonSerializer.Deserialize<MobileAccessResponse>(response, MobileAccessProtocol.Json)
                ?? throw new IOException("The RTX Lens service returned an empty Mobile Access response.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(response);
        }
    }

    private void RequireTrustedServer(NamedPipeClientStream pipe)
    {
        using var identity = WindowsIdentity.GetCurrent();

        var expected = debug ? identity.User : new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier));

        if (expected is null || !expected.Equals(owner))
        {
            throw new UnauthorizedAccessException("The Mobile Access channel does not belong to the expected RTX Lens service account.");
        }
    }
}