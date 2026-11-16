namespace Oficina.Contracts;

/// <summary>
/// Filas de comando deste serviço (docs/contratos-mensagens.md, seção 3). O nome é fixo
/// e é o mesmo que a saga do os-service usa no <c>Send</c>.
/// </summary>
public static class FilasExecucao
{
    public const string SolicitarDiagnostico = "execution-solicitar-diagnostico";
    public const string IniciarExecucao = "execution-iniciar-execucao";
    public const string CancelarExecucao = "execution-cancelar-execucao";
}
