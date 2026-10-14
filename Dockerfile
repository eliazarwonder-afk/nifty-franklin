# Multi-stage build for FlashKV Server
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy project files and restore dependencies
COPY FlashKV.sln Directory.Build.props ./
COPY src/FlashKV.Core/FlashKV.Core.csproj src/FlashKV.Core/
COPY src/FlashKV.Server/FlashKV.Server.csproj src/FlashKV.Server/
RUN dotnet restore src/FlashKV.Server/FlashKV.Server.csproj

# Copy source and publish optimized release
COPY src/FlashKV.Core/ src/FlashKV.Core/
COPY src/FlashKV.Server/ src/FlashKV.Server/
RUN dotnet publish src/FlashKV.Server/FlashKV.Server.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# Lightweight runtime image
FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS runtime
WORKDIR /app

# Non-root user for security
RUN addgroup -S flashkv && adduser -S flashkv -G flashkv
USER flashkv

COPY --from=build /app/publish .

# Default Redis port
EXPOSE 6379

ENTRYPOINT ["dotnet", "FlashKV.Server.dll"]
CMD ["--host", "0.0.0.0", "--port", "6379"]
