# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Accrue.Api -c Release -o /out /p:UseAppHost=false

# Chiseled: no shell, no package manager, non-root by default.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled
WORKDIR /app
COPY --from=build /out ./
ENV ASPNETCORE_URLS=http://+:8080     ConnectionStrings__Accrue="Data Source=/app/App_Data/accrue.db"
# Jwt__SigningKey MUST be supplied at runtime (>= 32 bytes); the app refuses to start without it.
VOLUME ["/app/App_Data"]
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Accrue.Api.dll"]
