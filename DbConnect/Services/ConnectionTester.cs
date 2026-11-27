using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>
/// Runs a provider's connection probe and turns its outcome into a <see cref="ConnectionTestResult"/>.
/// The provider supplies the probe (open, then read the server version) and its own exception classifier.
/// Transport-level causes every driver shares (sockets, timeouts, TLS) are checked first; the provider's
/// classifier then reads its driver's error codes.
/// </summary>
public static class ConnectionTester
{
    public static async Task<ConnectionTestResult> RunAsync(
        Func<Task<string>> openAndReadVersion,
        Func<Exception, ConnectionFailureCategory?> classify)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var version = await openAndReadVersion();
            return ConnectionTestResult.Succeeded(version, stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            return Failed(ex, stopwatch.Elapsed, classify);
        }
    }

    /// <summary>A failed result for <paramref name="exception"/>, classified by transport cause first.</summary>
    public static ConnectionTestResult Failed(
        Exception exception, TimeSpan elapsed, Func<Exception, ConnectionFailureCategory?>? classify = null)
    {
        var category = ClassifyTransport(exception) ?? classify?.Invoke(exception) ?? ConnectionFailureCategory.Other;
        return ConnectionTestResult.Failed(category, Describe(exception), elapsed,
            exception is CertificateFileException ? exception.Message : null);
    }

    /// <summary>
    /// SSH tunnel failures, then socket, timeout and TLS failures anywhere in the exception chain. An SSH failure
    /// keeps its SSH category even when its cause is a socket error, so it never reads as a database failure.
    /// </summary>
    private static ConnectionFailureCategory? ClassifyTransport(Exception exception)
    {
        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            switch (ex)
            {
                case CertificateFileException certificate:
                    return certificate.Category;
                case SshTunnelException ssh:
                    return ssh.Category;
                case TimeoutException:
                    return ConnectionFailureCategory.Timeout;
                case SocketException { SocketErrorCode: SocketError.TimedOut }:
                    return ConnectionFailureCategory.Timeout;
                case SocketException:
                    return ConnectionFailureCategory.HostUnreachable;
                case AuthenticationException:
                    return ConnectionFailureCategory.TlsCertificate;
            }
        }
        return null;
    }

    /// <summary>The full driver message: every exception in the chain, outermost first.</summary>
    private static string Describe(Exception exception)
    {
        var builder = new StringBuilder();
        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            if (builder.Length > 0)
                builder.AppendLine().Append("---> ");
            builder.Append(ex.GetType().FullName).Append(": ").Append(ex.Message);
        }
        return builder.ToString();
    }
}
