# --- Сборка ---
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/MyHealth.Api/MyHealth.Api.csproj ./MyHealth.Api/
RUN dotnet restore ./MyHealth.Api/MyHealth.Api.csproj
COPY src/MyHealth.Api/ ./MyHealth.Api/
RUN dotnet publish ./MyHealth.Api/MyHealth.Api.csproj -c Release -o /app /p:UseAppHost=false

# --- Рантайм ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./
# Kestrel слушает HTTP внутри контейнера; TLS терминирует Nginx снаружи.
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
ENTRYPOINT ["dotnet", "MyHealth.Api.dll"]
