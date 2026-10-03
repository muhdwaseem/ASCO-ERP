# ASCO for Render (or any Docker host): one container serving the API and the built React app.
# Uses the C-ERP snapshot in vendor/c-erp (refresh with deploy/refresh-cerp-vendor.ps1).

# 1) Front end
FROM node:22-alpine AS web
WORKDIR /web
COPY package.json package-lock.json ./
RUN npm ci
COPY index.html vite.config.ts tsconfig*.json ./
COPY public ./public
COPY src ./src
RUN npm run build

# 2) API
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS api
WORKDIR /src
COPY vendor ./vendor
COPY server/Asco.Api ./server/Asco.Api
RUN dotnet publish server/Asco.Api/Asco.Api.csproj -c Release -o /app -p:CErpSrc=/src/vendor/c-erp/src

# 3) Runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=api /app ./
COPY --from=web /web/dist ./wwwroot
# Render sets PORT; Program.cs binds 0.0.0.0:$PORT. Data lives in /data (a disk on paid plans, temporary on free).
# DOTNET_EnableWriteXorExecute=0: avoids a startup crash (exit 139) seen on some hosted container kernels.
ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_EnableWriteXorExecute=0 \
    DOTNET_TieredPGO=0 \
    Database__Provider=Sqlite \
    Database__SqliteJournalMode=DELETE \
    ConnectionStrings__Sqlite="Data Source=/data/asco.db" \
    Seed__DemoData=true \
    Security__BehindProxy=true \
    DataProtection__KeysPath=/data/keys \
    PORT=8080
RUN mkdir -p /data
EXPOSE 8080
ENTRYPOINT ["dotnet", "Asco.Api.dll"]
