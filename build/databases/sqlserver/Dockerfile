FROM mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04

USER root
RUN mkdir -p /usr/config
COPY entrypoint.sh /usr/config/entrypoint.sh
COPY seed.sql /usr/config/seed.sql
RUN chmod +x /usr/config/entrypoint.sh
USER mssql

ENTRYPOINT ["/usr/config/entrypoint.sh"]
