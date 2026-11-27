using System;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>A configured TLS certificate or key cannot be opened for reading.</summary>
public sealed class CertificateFileException(string path, Exception innerException)
    : Exception($"Certificate or key file is missing or unreadable: {path}", innerException)
{
    public ConnectionFailureCategory Category => ConnectionFailureCategory.CertificateFileMissing;
}
