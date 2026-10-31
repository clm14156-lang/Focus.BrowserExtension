using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FocusApp.Contracts;

public static class IpcProtocol
{
    public const int CurrentVersion = 2;
    public const int MaximumFrameBytes = 1024 * 1024;
    public const string DefaultPipeName = "FocusApp.Service.v2";

    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

public enum IpcMessageKind
{
    Request,
    Response,
    Event
}

public enum IpcClientRole
{
    Desktop,
    Agent
}

public enum IpcErrorCode
{
    InvalidRequest,
    ProtocolVersionMismatch,
    UnauthorizedClient,
    OperationNotSupported,
    BusinessRejected,
    PersistenceUnavailable,
    ServiceUnavailable,
    TimedOut,
    Cancelled,
    InternalError
}

public sealed record IpcErrorResult(IpcErrorCode Code, string Message, bool IsRetryable = false);

public sealed record IpcEnvelope(
    IpcMessageKind Kind,
    int ProtocolVersion,
    Guid RequestId,
    IpcClientRole? ClientRole,
    string Operation,
    JsonElement? Payload,
    IpcErrorResult? Error,
    long Sequence = 0)
{
    public static IpcEnvelope CreateRequest<T>(Guid requestId, IpcClientRole role, string operation, T payload)
        => new(
            IpcMessageKind.Request,
            IpcProtocol.CurrentVersion,
            requestId,
            role,
            operation,
            JsonSerializer.SerializeToElement(payload, IpcProtocol.JsonOptions),
            null);

    public static IpcEnvelope CreateSuccess<T>(IpcEnvelope request, T payload)
        => new(
            IpcMessageKind.Response,
            IpcProtocol.CurrentVersion,
            request.RequestId,
            null,
            request.Operation,
            JsonSerializer.SerializeToElement(payload, IpcProtocol.JsonOptions),
            null);

    public static IpcEnvelope CreateFailure(IpcEnvelope request, IpcErrorResult error)
        => new(
            IpcMessageKind.Response,
            IpcProtocol.CurrentVersion,
            request.RequestId,
            null,
            request.Operation,
            null,
            error);

    public static IpcEnvelope CreateEvent<T>(string operation, long sequence, T payload)
        => new(
            IpcMessageKind.Event,
            IpcProtocol.CurrentVersion,
            Guid.Empty,
            null,
            operation,
            JsonSerializer.SerializeToElement(payload, IpcProtocol.JsonOptions),
            null,
            sequence);

    public T ReadPayload<T>()
    {
        if (Payload is null)
        {
            throw new IpcProtocolException("IPC 消息缺少负载。");
        }

        return Payload.Value.Deserialize<T>(IpcProtocol.JsonOptions)
            ?? throw new IpcProtocolException("IPC 消息负载无法反序列化。");
    }
}

public static class IpcFrameCodec
{
    public static async Task WriteAsync(
        Stream stream,
        IpcEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        var content = JsonSerializer.SerializeToUtf8Bytes(envelope, IpcProtocol.JsonOptions);
        if (content.Length > IpcProtocol.MaximumFrameBytes)
        {
            throw new IpcProtocolException("IPC 消息超过允许大小。");
        }

        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, content.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(content, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<IpcEnvelope?> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        var header = new byte[sizeof(int)];
        var headerRead = await ReadExactlyOrEndAsync(stream, header, cancellationToken);
        if (!headerRead)
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > IpcProtocol.MaximumFrameBytes)
        {
            throw new IpcProtocolException("IPC 消息帧长度无效。");
        }

        var content = new byte[length];
        await stream.ReadExactlyAsync(content, cancellationToken);
        return JsonSerializer.Deserialize<IpcEnvelope>(content, IpcProtocol.JsonOptions)
            ?? throw new IpcProtocolException("IPC 消息无法反序列化。");
    }

    private static async Task<bool> ReadExactlyOrEndAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[totalRead..], cancellationToken);
            if (read == 0)
            {
                if (totalRead == 0)
                {
                    return false;
                }

                throw new EndOfStreamException("IPC 消息帧在读取完成前断开。");
            }

            totalRead += read;
        }

        return true;
    }
}

public sealed class IpcProtocolException : Exception
{
    public IpcProtocolException(string message) : base(message)
    {
    }
}

public sealed class IpcRemoteException : Exception
{
    public IpcRemoteException(IpcErrorResult error) : base(error.Message)
    {
        Error = error;
    }

    public IpcErrorResult Error { get; }
}

public sealed class IpcConnectionException : Exception
{
    public IpcConnectionException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
