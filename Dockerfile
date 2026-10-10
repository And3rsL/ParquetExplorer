# ---- Front-end assets (Tailwind CSS + CodeMirror) ----
FROM node:22-alpine AS assets
WORKDIR /src/ClientApp
COPY src/ParquetExplorer/ClientApp/package*.json ./
RUN npm ci
COPY src/ParquetExplorer/ClientApp/ ./
COPY src/ParquetExplorer/Components/ ../Components/
RUN npm run build

# ---- .NET build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/ParquetExplorer/ParquetExplorer.csproj src/ParquetExplorer/
RUN dotnet restore src/ParquetExplorer/ParquetExplorer.csproj
COPY src/ParquetExplorer/ src/ParquetExplorer/
COPY --from=assets /src/wwwroot/css/app.css src/ParquetExplorer/wwwroot/css/app.css
COPY --from=assets /src/wwwroot/js/editor.js src/ParquetExplorer/wwwroot/js/editor.js
RUN dotnet publish src/ParquetExplorer/ParquetExplorer.csproj -c Release --no-restore -o /app

# ---- Runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./
# Pre-install the DuckDB extensions needed for S3 so the container works without internet access to extensions.duckdb.org.
ENV ParquetExplorer__Query__ExtensionDirectory=/app/duckdb-extensions
RUN dotnet ParquetExplorer.dll install-extensions /app/duckdb-extensions
ENV ASPNETCORE_URLS=http://+:8080 \
    ParquetExplorer__StateDirectory=/state \
    ParquetExplorer__SourceManagement__AllowedLocalRoots__0=/data \
    ParquetExplorer__Query__TempDirectory=/tmp/parquet-explorer \
    ParquetExplorer__DataSources__0__Name=data \
    ParquetExplorer__DataSources__0__Type=Local \
    ParquetExplorer__DataSources__0__Path=/data
RUN mkdir -p /data /state && chown -R app:app /data /state /app/duckdb-extensions
USER app
EXPOSE 8080
VOLUME ["/state"]
ENTRYPOINT ["dotnet", "ParquetExplorer.dll"]
