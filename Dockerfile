# Dockerfile para LayoutParserApi - Servidor de Produção 172.25.32.42
# IMPORTANTE: A descriptografia requer LayoutParserDecrypt.exe (.NET Framework 4.8.1)
# Para Linux, use Windows Container ou solução alternativa (veja DEPLOY.md)

# Opção 1: Windows Container (recomendado para produção)
# FROM mcr.microsoft.com/dotnet/framework/aspnet:4.8-windowsservercore-ltsc2019 AS base

# Opção 2: Linux Container (requer executável via Wine ou serviço externo)
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app

# Criar diretórios para logs e dados
RUN mkdir -p /var/log/layoutparser /app/data/examples /app/data/rules /app/log

# Expor portas para produção
EXPOSE 8080
EXPOSE 8443

# Esta fase é usada para compilar o projeto de serviço
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["LayoutParserApi.csproj", "."]
RUN dotnet restore "./LayoutParserApi.csproj"
COPY . .
WORKDIR "/src/."
RUN dotnet build "./LayoutParserApi.csproj" -c $BUILD_CONFIGURATION -o /app/build

# Esta fase é usada para publicar o projeto de serviço a ser copiado para a fase final
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./LayoutParserApi.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# Esta fase é usada na produção
FROM base AS final
WORKDIR /app

# Copiar arquivos publicados
COPY --from=publish /app/publish .

# IMPORTANTE: Copiar LayoutParserDecrypt.exe e dependências
# Se estiver em Linux, você precisará de uma solução alternativa (Wine, serviço externo, etc.)
# Para Windows Container, copie os arquivos do build do LayoutParserDecrypt
# COPY --from=decrypt-build /path/to/LayoutParserDecrypt.exe ./
# (LayoutParserLib.dll não é mais necessária: a lib foi arquivada; a criptografia vive no layoutparser-decrypt)

# Configurar variáveis de ambiente para produção
ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://0.0.0.0:8080;https://0.0.0.0:8443

# Configurar Ollama para servidor remoto
ENV OLLAMA_URL=http://172.25.32.42:11434
ENV OLLAMA_MODEL=llama3.1:latest

# Nota: A descriptografia REQUER LayoutParserDecrypt.exe (.NET Framework 4.8.1)
# Configure o caminho em appsettings.json: "LayoutParserDecrypt:Path"
# Para Linux: considere usar Windows Container ou serviço externo de descriptografia

ENTRYPOINT ["dotnet", "LayoutParserApi.dll"]