namespace Oficina.Execution.Domain;

/// <summary>A operação não vale na etapa atual da execução (a API responde 409).</summary>
public sealed class EtapaInvalidaException : Exception
{
    public EtapaInvalidaException(string message) : base(message)
    {
    }

    public EtapaInvalidaException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public EtapaInvalidaException()
    {
    }
}
