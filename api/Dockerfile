# ============================================================
# Build stage
# ============================================================
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ExcelDatasetManager.Api.csproj ./
RUN dotnet restore ExcelDatasetManager.Api.csproj

COPY . ./
RUN dotnet publish ExcelDatasetManager.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

# ============================================================
# Runtime stage
# ============================================================
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app

# DuckDB needs libicu, and we set InvariantGlobalization=false in csproj so .NET also requires it.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libicu-dev \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish ./

# Storage volume mount point.
RUN mkdir -p /app/storage && chmod 755 /app/storage
VOLUME ["/app/storage"]

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
ENV Storage__RootPath=/app/storage

ENTRYPOINT ["dotnet", "ExcelDatasetManager.Api.dll"]
