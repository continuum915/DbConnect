using System;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>A failed explicit transaction end, with the evidence available before cleanup.</summary>
public sealed class TransactionBoundaryException : Exception
{
    public TransactionBoundaryException(TransactionResult result, Exception innerException)
        : base(result.DiagnosticMessage, innerException) => Result = result;

    public TransactionResult Result { get; }
}
