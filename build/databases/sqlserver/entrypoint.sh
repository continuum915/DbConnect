#!/usr/bin/env bash
set -euo pipefail

/opt/mssql/bin/sqlservr &
server_pid=$!

if [ -x /opt/mssql-tools18/bin/sqlcmd ]; then
    sqlcmd="/opt/mssql-tools18/bin/sqlcmd"
    trust_flag="-C"
else
    sqlcmd="/opt/mssql-tools/bin/sqlcmd"
    trust_flag=""
fi

echo "Waiting for SQL Server to accept connections..."
for _ in {1..60}; do
    if "$sqlcmd" -S localhost -U sa -P "$MSSQL_SA_PASSWORD" $trust_flag -Q "SELECT 1" >/dev/null 2>&1; then
        break
    fi
    sleep 2
done

if ! "$sqlcmd" -S localhost -U sa -P "$MSSQL_SA_PASSWORD" $trust_flag -Q "SELECT 1" >/dev/null 2>&1; then
    echo "SQL Server did not become ready in time." >&2
    exit 1
fi

# seed.sql cannot guard itself: RETURN only ends its own batch, so on an
# already-seeded volume the later batches would fail and stop the container.
#
# The server accepts connections before it has started existing user databases
# (sys.databases already says ONLINE, but touching them fails with error 904),
# so retry the check itself until it gives a real answer.
seeded=""
for _ in {1..60}; do
    seeded=$("$sqlcmd" -S localhost -U sa -P "$MSSQL_SA_PASSWORD" $trust_flag -h -1 -W \
        -Q "SET NOCOUNT ON; SELECT CASE WHEN OBJECT_ID(N'rain_ecommerce.dbo.Vendors', N'U') IS NOT NULL THEN 1 ELSE 0 END" \
        2>/dev/null | tr -d '[:space:]')
    if [ "$seeded" = "0" ] || [ "$seeded" = "1" ]; then
        break
    fi
    sleep 2
done

if [ "$seeded" != "0" ] && [ "$seeded" != "1" ]; then
    echo "Could not determine whether rain_ecommerce is seeded." >&2
    exit 1
fi

if [ "$seeded" = "1" ]; then
    echo "rain_ecommerce is already seeded. Skipping seed script."
else
    echo "Applying DbConnect seed schema/data..."
    "$sqlcmd" -S localhost -U sa -P "$MSSQL_SA_PASSWORD" $trust_flag -b -i /usr/config/seed.sql
fi

wait "$server_pid"
