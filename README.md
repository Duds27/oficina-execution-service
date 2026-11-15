# oficina-execution-service

Microsserviço de **diagnóstico e execução** da Oficina Mecânica (FIAP 15SOAT, Fase 4).

> **Estado atual:** esqueleto da [Etapa 0](https://github.com/Duds27/tech-challenge-fiap/blob/main/docs/fase4/etapa-0-fundacao.md). A API tem health checks, logs estruturados, autenticação JWT e Swagger. O domínio, o DynamoDB e a mensageria entram na [Etapa 2](https://github.com/Duds27/tech-challenge-fiap/blob/main/docs/fase4/etapa-2-execution-service.md).

## Responsabilidades

- Manter a fila de trabalho da oficina, criada a partir dos comandos `SolicitarDiagnostico` e `IniciarExecucao`.
- Registrar o diagnóstico e o reparo (apontamentos e linha do tempo).
- Avisar o `os-service` de cada avanço: diagnóstico iniciado e concluído, execução iniciada, rejeitada e finalizada.
- Cancelar a execução quando a saga mandar (`CancelarExecucao`).

**Não é dele:**

- Orçamento e pagamento: são do `billing-service`.
- Cadastro e ciclo de vida da OS: são do `os-service`.

Os dados de que precisa chegam dentro das mensagens. Ver os [contratos de mensagens](https://github.com/Duds27/tech-challenge-fiap/blob/main/docs/contratos-mensagens.md).

## Estrutura

```
src/
  Oficina.Execution.Domain/          entidade Execucao e suas etapas
  Oficina.Execution.Application/     casos de uso e portas
  Oficina.Execution.Infrastructure/  DynamoDB (single-table + outbox próprio), MassTransit
  Oficina.Execution.API/             controllers, health checks
tests/
  Oficina.Execution.UnitTests/
  Oficina.Execution.IntegrationTests/
```

- **Versões:** os pacotes ficam centralizados em `Directory.Packages.props`.
- **Avisos:** no código de `src/`, todo aviso é tratado como erro (`src/Directory.Build.props`).
- **SDK:** o `global.json` fixa o .NET 8.

## Como rodar

Testes:

```bash
dotnet test
```

Serviço + DynamoDB Local + RabbitMQ:

```bash
docker compose up --build
```

- Health checks: http://localhost:8083/health/live e http://localhost:8083/health/ready
- Swagger: http://localhost:8083/swagger
- RabbitMQ: http://localhost:15672 (usuário `oficina`, senha `oficina_dev`)

Sem Docker: `dotnet run --project src/Oficina.Execution.API` (porta 8083).

## CI

O `.github/workflows/ci.yml` roda em PR e push na `main`: restore, build, testes com cobertura e build da imagem Docker.

- O job `build-test-quality` é o *required check* da proteção da `main`.
- Cobertura mínima de 80% e SonarCloud entram na [Etapa 4](https://github.com/Duds27/tech-challenge-fiap/blob/main/docs/fase4/etapa-4-plataforma-cicd.md).
