# Imagen de la API (turning.API) sobre Azure Linux 3.0.
# Build:  docker build -t turning-api .
# Uso local con SQL Server: ver docker-compose.yml

# --- Build ---
FROM mcr.microsoft.com/dotnet/sdk:10.0-azurelinux3.0 AS build
WORKDIR /src

# Primero solo los .csproj: el restore queda en cache mientras no cambien dependencias.
COPY src/turning.Domain/turning.Domain.csproj src/turning.Domain/
COPY src/turning.Application/turning.Application.csproj src/turning.Application/
COPY src/turning.Infrastructure/turning.Infrastructure.csproj src/turning.Infrastructure/
COPY src/turning.API/turning.API.csproj src/turning.API/
RUN dotnet restore src/turning.API/turning.API.csproj

COPY src/turning.Domain/ src/turning.Domain/
COPY src/turning.Application/ src/turning.Application/
COPY src/turning.Infrastructure/ src/turning.Infrastructure/
COPY src/turning.API/ src/turning.API/
RUN dotnet publish src/turning.API/turning.API.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

# --- Runtime ---
# distroless-extra: sin shell, usuario no root (app) y con ICU + tzdata, que SqlClient necesita.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-azurelinux3.0-distroless-extra AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "turning.API.dll"]
