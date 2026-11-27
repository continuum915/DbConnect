FROM mysql:8.4.3

COPY init/001_seed.sql /docker-entrypoint-initdb.d/001_seed.sql
COPY init/002_scratch_databases.sql /docker-entrypoint-initdb.d/002_scratch_databases.sql

# The integration lane creates triggers as the non-SUPER rain user; with binary logging on
# MySQL refuses that unless function creators are trusted.
CMD ["mysqld", "--log-bin-trust-function-creators=1"]
