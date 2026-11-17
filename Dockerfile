# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Arquivos de projeto primeiro, para aproveitar o cache do restore
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Directory.Build.props src/
COPY src/Oficina.Execution.Domain/Oficina.Execution.Domain.csproj src/Oficina.Execution.Domain/
COPY src/Oficina.Execution.Application/Oficina.Execution.Application.csproj src/Oficina.Execution.Application/
COPY src/Oficina.Execution.Infrastructure/Oficina.Execution.Infrastructure.csproj src/Oficina.Execution.Infrastructure/
COPY src/Oficina.Execution.API/Oficina.Execution.API.csproj src/Oficina.Execution.API/
RUN dotnet restore src/Oficina.Execution.API/Oficina.Execution.API.csproj

COPY src/ src/
RUN dotnet publish src/Oficina.Execution.API/Oficina.Execution.API.csproj -c Release -o /publish --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /publish .

# Usuário não-root 'app' (UID 1654), já presente nas imagens .NET 8.
USER $APP_UID

# Observabilidade: o OpenTelemetry da aplicação manda traces, métricas e logs por OTLP
# (src/Oficina.Execution.API/Observabilidade). SERVICE_VERSION vira o service.version; o CD passa o SHA.
ARG SERVICE_VERSION=dev
ENV SERVICE_VERSION=${SERVICE_VERSION}

EXPOSE 8080
ENTRYPOINT ["dotnet", "Oficina.Execution.API.dll"]
