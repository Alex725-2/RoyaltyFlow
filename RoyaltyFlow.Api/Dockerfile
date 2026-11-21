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
RUN dotnet publish RoyaltyFlow.Api/RoyaltyFlow.Api.csproj -c Release --no-restore -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim
WORKDIR /app
COPY --from=build /out/ ./
ENV ASPNETCORE_URLS=http://+:8080 \
    Imports__SpoolDirectory=/tmp/royaltyflow-uploads
EXPOSE 8080
USER $APP_UID
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD ["dotnet", "RoyaltyFlow.Api.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "RoyaltyFlow.Api.dll"]
