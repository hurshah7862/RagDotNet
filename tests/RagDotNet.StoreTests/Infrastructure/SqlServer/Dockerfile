# SQL Server 2025 with Full-Text Search, which the official image leaves out. The connector needs it: RagDotNet marks the
# chunk text IsFullTextIndexed, and native hybrid search uses FREETEXTTABLE. The package version is the server's exact build.
ARG BASE_IMAGE
FROM ${BASE_IMAGE}
USER root
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl gnupg ca-certificates \
 && curl -fsSL https://packages.microsoft.com/keys/microsoft.asc | gpg --batch --yes --dearmor -o /usr/share/keyrings/microsoft-prod.gpg \
 && curl -fsSL https://packages.microsoft.com/config/ubuntu/24.04/mssql-server-2025.list -o /etc/apt/sources.list.d/mssql-server-2025.list \
 && apt-get update \
 && apt-get install -y --no-install-recommends mssql-server-fts=17.0.5065.9-3 \
 && rm -rf /var/lib/apt/lists/*
USER mssql
