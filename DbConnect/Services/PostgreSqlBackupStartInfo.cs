using System;
using System.Diagnostics;

namespace DbConnect.Services;

public static class PostgreSqlBackupStartInfo
{
    public static ProcessStartInfo CreateBackup(string connectionString, string outputPath)
    {
        var info = Create("pg_dump", connectionString);
        info.ArgumentList.Add("--format=custom");
        info.ArgumentList.Add($"--file={outputPath}");
        return info;
    }

    public static ProcessStartInfo CreateRestore(string connectionString, string inputPath)
    {
        var info = Create("pg_restore", connectionString);
        info.ArgumentList.Add("--");
        info.ArgumentList.Add(inputPath);
        return info;
    }

    private static ProcessStartInfo Create(string executable, string connectionString)
    {
        var (host, port, database, username, password) = ParsePostgresConnection(connectionString);
        var info = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // Attached option values cannot be interpreted as additional options.
        info.ArgumentList.Add($"--host={host}");
        info.ArgumentList.Add($"--port={port}");
        info.ArgumentList.Add($"--username={username}");
        info.ArgumentList.Add($"--dbname={database}");
        info.Environment["PGPASSWORD"] = password;
        return info;
    }

    private static (string host, string port, string database, string username, string password)
        ParsePostgresConnection(string connectionString)
    {
        // Use NpgsqlConnectionStringBuilder if available, otherwise parse manually
        string host = "localhost", port = "5432", database = "", username = "", password = "";
        try
        {
            var builder = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
            host = builder.Host ?? "localhost";
            port = (builder.Port > 0 ? builder.Port : 5432).ToString();
            database = builder.Database ?? "";
            username = builder.Username ?? "";
            password = builder.Password ?? "";
        }
        catch
        {
            // Fallback: parse key=value pairs
            foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var idx = part.IndexOf('=');
                if (idx < 0) continue;
                var key = part[..idx].Trim().ToLowerInvariant();
                var val = part[(idx + 1)..].Trim();
                switch (key)
                {
                    case "host": host = val; break;
                    case "port": port = val; break;
                    case "database": database = val; break;
                    case "username":
                    case "user id":
                    case "user": username = val; break;
                    case "password": password = val; break;
                }
            }
        }
        return (host, port, database, username, password);
    }
}
