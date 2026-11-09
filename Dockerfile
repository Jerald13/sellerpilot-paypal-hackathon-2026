# SellerPilot hosted demo: one container that serves the React UI and the API from the same origin.
# Built by Render from render.yaml; also works with `docker build -t sellerpilot . && docker run -p 8080:8080 --env-file .env sellerpilot`.

# 1. React UI
FROM node:22-alpine AS web
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
RUN npm run build

# 2. API
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api
WORKDIR /src
COPY src/SellerPilot.Api/SellerPilot.Api.csproj src/SellerPilot.Api/
RUN dotnet restore src/SellerPilot.Api/SellerPilot.Api.csproj
COPY src/ src/
RUN dotnet publish src/SellerPilot.Api/SellerPilot.Api.csproj -c Release -o /app --no-restore

# 3. Runtime (runs as the image's non-root "app" user)
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=api /app ./
COPY --from=web /web/dist ./wwwroot
# SQLite lives in /tmp: the demo database is rebuilt from the seed file on every start.
ENV ConnectionStrings__SellerPilot="Data Source=/tmp/sellerpilot.db" \
    ASPNETCORE_FORWARDEDHEADERS_ENABLED=true
EXPOSE 8080
# Render passes the port in $PORT; locally it defaults to 8080.
CMD ["sh", "-c", "ASPNETCORE_URLS=http://+:${PORT:-8080} exec dotnet SellerPilot.Api.dll"]
