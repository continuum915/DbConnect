using System;

namespace DbConnect.Models;

public enum BlobEditAction { SetBytes, SetNull, SetEmpty }

public sealed class BlobEditResult
{
    public static readonly BlobEditResult Null = new(BlobEditAction.SetNull, null);
    public static readonly BlobEditResult Empty = new(BlobEditAction.SetEmpty, Array.Empty<byte>());

    public BlobEditAction Action { get; }
    public byte[]? Bytes { get; }

    private BlobEditResult(BlobEditAction action, byte[]? bytes)
    {
        Action = action;
        Bytes = bytes;
    }

    public static BlobEditResult FromBytes(byte[] bytes) => new(BlobEditAction.SetBytes, bytes);
}
