using System;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using DuckDB.NET.Data;
using DuckDB.NET.Native;

namespace DbConnect.Services;

/// <summary>Streaming safeguards for the pinned DuckDB.NET 1.3.0 driver.</summary>
internal static class DuckDbQueryExport
{
    public static IDisposable Register(DbCommand command, CancellationToken token)
    {
        ((DuckDBCommand)command).UseStreamingMode = true;
        return new Cancellation(command, token);
    }

    public static void ValidateCompletion(DbDataReader reader)
    {
        // 1.3.0 treats a failed native chunk fetch as EOF. Check before NextResult/Close
        // destroys the native result, otherwise a partial export can be published as success.
        // No public result/error accessor exists in this version. A changed driver layout
        // fails closed (MissingFieldException) until this adapter is updated and verified.
        ref var result = ref CurrentResult((DuckDBDataReader)reader);
        var error = NativeMethods.Query.DuckDBResultError(ref result);
        if (error != IntPtr.Zero)
            throw new InvalidOperationException(Marshal.PtrToStringUTF8(error) ?? "DuckDB export reader failed.");
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "currentResult")]
    private static extern ref DuckDBResult CurrentResult(DuckDBDataReader reader);

    // This driver stores CommandTimeout but does not enforce it during native execution.
    private sealed class Cancellation : IDisposable
    {
        private readonly CancellationTokenSource _stop;
        private readonly CancellationTokenRegistration _registration;

        public Cancellation(DbCommand command, CancellationToken token)
        {
            _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            _registration = _stop.Token.Register(command.Cancel);
            if (command.CommandTimeout > 0) _stop.CancelAfter(TimeSpan.FromSeconds(command.CommandTimeout));
        }

        public void Dispose()
        {
            _registration.Dispose();
            _stop.Dispose();
        }
    }
}
