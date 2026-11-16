using Amazon.DynamoDBv2;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Oficina.Execution.Infrastructure.Persistence;

/// <summary>
/// Cria a tabela no DynamoDB Local, só no ambiente Development. Na AWS quem cria é o Terraform
/// (<c>infra/</c>), e a role do pod nem tem permissão de criar tabela.
/// Registrado antes do MassTransit: os consumidores só começam com a tabela pronta.
/// </summary>
internal sealed partial class CriadorTabelaDesenvolvimento : IHostedService
{
    private const int Tentativas = 15;

    private readonly IAmazonDynamoDB _dynamo;
    private readonly IHostEnvironment _ambiente;
    private readonly ILogger<CriadorTabelaDesenvolvimento> _logger;
    private readonly string _tabela;

    public CriadorTabelaDesenvolvimento(
        IAmazonDynamoDB dynamo,
        IOptions<DynamoDbOptions> opcoes,
        IHostEnvironment ambiente,
        ILogger<CriadorTabelaDesenvolvimento> logger)
    {
        _dynamo = dynamo;
        _ambiente = ambiente;
        _logger = logger;
        _tabela = opcoes.Value.TableName;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_ambiente.IsDevelopment())
            return;

        // No docker-compose, a API pode subir antes de o DynamoDB Local aceitar conexões.
        for (var tentativa = 1; ; tentativa++)
        {
            try
            {
                var criou = await TabelaExecucoes.CriarSeNaoExistirAsync(_dynamo, _tabela, cancellationToken);
                LogTabelaPronta(_tabela, criou);
                return;
            }
            catch (Exception ex) when (tentativa < Tentativas && ex is not OperationCanceledException)
            {
                LogTentativaFalhou(tentativa, _tabela, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Tabela {Tabela} pronta no DynamoDB Local (criada agora: {Criada})")]
    private partial void LogTabelaPronta(string tabela, bool criada);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tentativa {Tentativa} de preparar a tabela {Tabela} falhou: {Erro}")]
    private partial void LogTentativaFalhou(int tentativa, string tabela, string erro);
}
