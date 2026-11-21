FROM node:24-bookworm-slim AS dashboard
WORKDIR /frontend
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci --include=dev --no-audit --no-fund
COPY frontend/ ./
RUN npm test && npm run build && chmod -R a=rX /frontend/dist

FROM mcr.microsoft.com/dotnet/sdk:8.0.428 AS build
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY RoyaltyFlow.Api/RoyaltyFlow.Api.csproj RoyaltyFlow.Api/
COPY RoyaltyFlow.Application/RoyaltyFlow.Application.csproj RoyaltyFlow.Application/
COPY RoyaltyFlow.Domain/RoyaltyFlow.Domain.csproj RoyaltyFlow.Domain/
COPY RoyaltyFlow.Infrastructure/RoyaltyFlow.Infrastructure.csproj RoyaltyFlow.Infrastructure/
COPY docs/schema.sql docs/schema.sql
RUN dotnet restore RoyaltyFlow.Api/RoyaltyFlow.Api.csproj
COPY RoyaltyFlow.Api/ RoyaltyFlow.Api/
COPY RoyaltyFlow.Application/ RoyaltyFlow.Application/
COPY RoyaltyFlow.Domain/ RoyaltyFlow.Domain/
COPY RoyaltyFlow.Infrastructure/ RoyaltyFlow.Infrastructure/
# A private checkout can supply owner-only modes. Normalize the published tree for APP_UID.
RUN dotnet publish RoyaltyFlow.Api/RoyaltyFlow.Api.csproj -c Release --no-restore -o /out /p:UseAppHost=false \
    && chmod -R a=rX /out

FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim
WORKDIR /app
COPY --from=build --chown=0:0 /out/ ./
COPY --from=dashboard --chown=0:0 /frontend/dist/ ./wwwroot/
ENV ASPNETCORE_URLS=http://+:8080 \
    Imports__SpoolDirectory=/tmp/royaltyflow-uploads
EXPOSE 8080
USER $APP_UID
RUN test "$(id -u)" -ne 0 \
    && test -r appsettings.json \
    && test -r appsettings.Development.json \
    && test -r wwwroot/index.html \
    && test -d wwwroot/assets \
    && inaccessible="$(find /app ! -readable -o -type d ! -executable)" \
    && { test -z "$inaccessible" || { printf 'Inaccessible runtime paths:\n%s\n' "$inaccessible" >&2; exit 1; }; }
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD ["dotnet", "RoyaltyFlow.Api.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "RoyaltyFlow.Api.dll"]
