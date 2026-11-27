using System;
using System.ComponentModel;
using System.Linq;
using Microsoft.Data.SqlClient;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>Maps SqlClient exceptions from a connection test to a <see cref="ConnectionFailureCategory"/>.</summary>
public static class SqlServerConnectionFailures
{
    // Schannel/CryptoAPI codes for an untrusted, expired or mismatched server certificate.
    private static readonly int[] CertificateErrors = [unchecked((int)0x80090325), unchecked((int)0x800B0109), unchecked((int)0x80090322), unchecked((int)0x800B0101)];

    public static ConnectionFailureCategory? Classify(Exception exception)
    {
        if (exception is not SqlException sql)
            return null;

        var numbers = sql.Errors.Cast<SqlError>().Select(error => error.Number).Append(sql.Number).ToHashSet();
        // A login naming a missing database reports 4060 alongside the generic 18456.
        if (numbers.Contains(4060))
            return ConnectionFailureCategory.DatabaseNotFound;
        if (numbers.Contains(18456))
            return ConnectionFailureCategory.Authentication;
        if (numbers.Overlaps([-2, 258, 10060]))
            return ConnectionFailureCategory.Timeout;
        if (numbers.Overlaps(CertificateErrors) || HasWin32Error(sql, CertificateErrors))
            return ConnectionFailureCategory.TlsCertificate;
        // 53/40/35/26 are SNI "server not found / could not open a connection"; 10061 refused; 11001/11004 DNS.
        // Managed SNI may omit the native code for fatal connection failures: number/state 0, class 20.
        if (numbers.Overlaps([53, 40, 35, 26, 10061, 11001, 11004]) || sql is { Number: 0, State: 0, Class: 20 })
            return ConnectionFailureCategory.HostUnreachable;
        return null;
    }

    private static bool HasWin32Error(Exception exception, int[] codes)
    {
        for (var ex = exception.InnerException; ex is not null; ex = ex.InnerException)
        {
            if (ex is Win32Exception win32 && codes.Contains(win32.NativeErrorCode))
                return true;
        }
        return false;
    }
}
