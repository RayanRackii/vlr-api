# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Core/Platform.Core.Domain/Platform.Core.Domain.csproj Core/Platform.Core.Domain/
COPY Core/Platform.Core.Infrastructure/Platform.Core.Infrastructure.csproj Core/Platform.Core.Infrastructure/
COPY Platform.Api/Platform.Api.csproj Platform.Api/

RUN dotnet restore Platform.Api/Platform.Api.csproj

COPY . .
RUN dotnet publish Platform.Api/Platform.Api.csproj \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "Platform.Api.dll"]
