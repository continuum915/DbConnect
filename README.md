# DbConnect

DbConnect is a cross-platform desktop app for working with SQL databases. It is built with
[Avalonia](https://avaloniaui.net/) on .NET 10 and runs on Windows, macOS and Linux.

## Supported databases

- SQLite
- PostgreSQL
- SQL Server
- MySQL
- DuckDB

## Features

- Connection manager with saved, encrypted connection settings
- Schema navigator for tables, views, routines and other objects
- SQL editor with syntax highlighting, schema-aware autocomplete and execution plans
- Editable data grid; changes are previewed as SQL and committed in a transaction
- Table designer that generates CREATE/ALTER/DROP DDL for each provider
- Schema and data comparison across connections
- Import from CSV and export to CSV, JSON, Excel and SQL
- Database dump, backup and restore
- ER diagrams
- Light and dark themes

## Getting started

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet restore DbConnect.slnx
dotnet build DbConnect.slnx --no-restore
dotnet run --project DbConnect/DbConnect.csproj
```

To create a sample e-commerce SQLite database to explore:

```powershell
cd DbConnect.DatabaseScaffolder
dotnet run
```

## Tests

```powershell
dotnet test DbConnect.slnx
```

The provider conformance tests for PostgreSQL, SQL Server and MySQL need running database servers.
`build/databases/compose.yaml` starts them in containers, and `build/databases/run-provider-integration.ps1`
runs the tests against them.

## Repository layout

| Path | Contents |
|------|----------|
| `DbConnect/` | The desktop app (views, view models, services, models) |
| `DbConnect.Tests/` | xUnit test suite |
| `DbConnect.PerformanceTests/` | Opt-in performance and load tests |
| `DbConnect.DatabaseScaffolder/` | Console tool that seeds a sample SQLite database |
| `Theme/` | The app's Avalonia theme |
| `ThirdParty/` | Patched forks of TextMateSharp and AvaloniaEdit.TextMate (MIT, see their LICENSE files) |
| `build/` | Packaging and test scripts for Windows, macOS and Linux |

## License

DbConnect is released under the [MIT License](LICENSE). The projects under `ThirdParty/` keep their own licenses.
