# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["src/PartnerIntegration.Api/PartnerIntegration.Api.csproj", "src/PartnerIntegration.Api/"]
COPY ["tests/PartnerIntegration.Tests/PartnerIntegration.Tests.csproj", "tests/PartnerIntegration.Tests/"]
RUN dotnet restore "src/PartnerIntegration.Api/PartnerIntegration.Api.csproj"

COPY . .
RUN dotnet publish "src/PartnerIntegration.Api/PartnerIntegration.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

# ---------- runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
EXPOSE 8080

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Development \
    DOTNET_EnableDiagnostics=0

RUN adduser --disabled-password --gecos "" --uid 5678 appuser && chown -R appuser /app
USER appuser

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "PartnerIntegration.Api.dll"]
