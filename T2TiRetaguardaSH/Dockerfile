# --- Estágio 1: Build --- 
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build 
WORKDIR /src 

# Copia o arquivo .csproj primeiro e restaura as dependências 
COPY ["T2TiRetaguardaSH.csproj", "."] 
RUN dotnet restore "./T2TiRetaguardaSH.csproj" 

# Copia todo o resto do código-fonte 
COPY . . 

# Compila e publica a aplicação 
RUN dotnet publish "T2TiRetaguardaSH.csproj" -c Release -o /app/publish

# --- Estágio 2: Final (Execução) --- 
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final 
WORKDIR /app 
COPY --from=build /app/publish . 
COPY --from=build /src/hibernate.cfg.xml . 
ENTRYPOINT ["dotnet", "T2TiRetaguardaSH.dll"]