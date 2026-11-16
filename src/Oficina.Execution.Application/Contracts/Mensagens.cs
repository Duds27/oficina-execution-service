using MassTransit;

// Contratos de mensagens v1 (docs/contratos-mensagens.md, seção 2.1), só as que o
// execution-service consome ou publica. Copiados sem mudança do os-service: o mesmo
// namespace, URN e exchange nos três serviços. Mudar um nome ou tipo aqui quebra outro
// repositório. Mudança incompatível vira v2 (seção 1.3).
namespace Oficina.Contracts;

// ───────────── Envelope (não vira exchange no RabbitMQ) ─────────────
[ExcludeFromTopology]
public interface IMensagemOS
{
    Guid CorrelationId { get; }
    int OsId { get; }
    string NumeroOS { get; }
    DateTime OcorridoEm { get; }
}

[ExcludeFromTopology]
public abstract record MensagemOS : IMensagemOS
{
    public Guid CorrelationId { get; init; }
    public int OsId { get; init; }
    public string NumeroOS { get; init; } = string.Empty;
    public DateTime OcorridoEm { get; init; }
}

// ───────────── Tipos auxiliares ─────────────
public record VeiculoInfo
{
    public string Placa { get; init; } = string.Empty;
    public string Marca { get; init; } = string.Empty;
    public string Modelo { get; init; } = string.Empty;
    public int Ano { get; init; }
}

public record ServicoExecucaoInfo
{
    public string Descricao { get; init; } = string.Empty;
    public decimal Quantidade { get; init; }
}

// ───────────── os → execution ─────────────
[MessageUrn("oficina:solicitar-diagnostico:v1"), EntityName("oficina.solicitar-diagnostico.v1")]
public record SolicitarDiagnostico : MensagemOS
{
    public VeiculoInfo Veiculo { get; init; } = new();
    public string? Descricao { get; init; }
}

[MessageUrn("oficina:iniciar-execucao:v1"), EntityName("oficina.iniciar-execucao.v1")]
public record IniciarExecucao : MensagemOS
{
    public IReadOnlyList<ServicoExecucaoInfo> Servicos { get; init; } = [];
}

[MessageUrn("oficina:cancelar-execucao:v1"), EntityName("oficina.cancelar-execucao.v1")]
public record CancelarExecucao : MensagemOS
{
    public string Motivo { get; init; } = string.Empty;
}

// ───────────── execution → os ─────────────
[MessageUrn("oficina:diagnostico-iniciado:v1"), EntityName("oficina.diagnostico-iniciado.v1")]
public record DiagnosticoIniciado : MensagemOS
{
    public string Mecanico { get; init; } = string.Empty;
}

[MessageUrn("oficina:diagnostico-concluido:v1"), EntityName("oficina.diagnostico-concluido.v1")]
public record DiagnosticoConcluido : MensagemOS
{
    public string? Observacoes { get; init; }
}

[MessageUrn("oficina:execucao-iniciada:v1"), EntityName("oficina.execucao-iniciada.v1")]
public record ExecucaoIniciada : MensagemOS;

[MessageUrn("oficina:execucao-rejeitada:v1"), EntityName("oficina.execucao-rejeitada.v1")]
public record ExecucaoRejeitada : MensagemOS
{
    public string Motivo { get; init; } = string.Empty;
}

[MessageUrn("oficina:execucao-finalizada:v1"), EntityName("oficina.execucao-finalizada.v1")]
public record ExecucaoFinalizada : MensagemOS
{
    public int DuracaoSegundos { get; init; }
}
