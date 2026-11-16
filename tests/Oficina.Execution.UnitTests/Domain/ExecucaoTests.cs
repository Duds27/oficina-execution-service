using Oficina.Execution.Domain;

namespace Oficina.Execution.UnitTests.Domain;

public class ExecucaoTests
{
    private static readonly DateTime T0 = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Correlacao = Guid.Parse("3f2c9a4e-8d1b-4c55-9a7e-2b6f0d9e1a11");
    private static readonly Veiculo Gol = new("ABC1D23", "VW", "Gol", 2020);
    private static readonly ServicoPrevisto[] Servicos = [new("Troca de óleo", 1m)];

    private static Execucao Nova() => Execucao.Abrir(42, Correlacao, "OS-20261116-0042", Gol, "Barulho no motor", T0);

    private static Execucao EmEtapa(EtapaExecucao etapa)
    {
        var e = Nova();
        if (etapa == EtapaExecucao.DiagnosticoPendente) return e;
        e.IniciarDiagnostico("joao", T0.AddMinutes(1));
        if (etapa == EtapaExecucao.EmDiagnostico) return e;
        e.ConcluirDiagnostico("Trocar óleo", "joao", T0.AddMinutes(2));
        if (etapa == EtapaExecucao.DiagnosticoConcluido) return e;
        e.IniciarExecucao(Servicos, T0.AddMinutes(3));
        if (etapa == EtapaExecucao.AguardandoExecucao) return e;
        e.IniciarReparo("joao", T0.AddMinutes(4));
        if (etapa == EtapaExecucao.EmReparo) return e;
        if (etapa == EtapaExecucao.Finalizada)
        {
            e.Finalizar("joao", T0.AddMinutes(10));
            return e;
        }

        e.Cancelar("teste", T0.AddMinutes(5));
        return e;
    }

    [Fact]
    public void Abrir_entra_na_fila_de_diagnostico_com_os_dados_da_OS()
    {
        var e = Nova();

        Assert.Equal(EtapaExecucao.DiagnosticoPendente, e.Etapa);
        Assert.Equal(42, e.OsId);
        Assert.Equal(Correlacao, e.CorrelationId);
        Assert.Equal(Gol, e.Estado.Veiculo);
        Assert.Equal("Barulho no motor", e.Estado.Descricao);
        Assert.Equal(T0, e.Estado.EtapaDesde);
        Assert.Equal(0, e.Version);
        var abertura = Assert.Single(e.Apontamentos);
        Assert.Equal(TipoApontamento.Abertura, abertura.Tipo);
        Assert.Same(abertura, Assert.Single(e.NovosApontamentos));
    }

    [Fact]
    public void Caminho_feliz_passa_por_todas_as_etapas_e_registra_a_linha_do_tempo()
    {
        var e = Nova();

        e.IniciarDiagnostico("joao", T0.AddMinutes(1));
        Assert.Equal(EtapaExecucao.EmDiagnostico, e.Etapa);
        Assert.Equal("joao", e.Estado.Mecanico);

        e.ConcluirDiagnostico("  Trocar óleo  ", "joao", T0.AddMinutes(2));
        Assert.Equal(EtapaExecucao.DiagnosticoConcluido, e.Etapa);
        Assert.Equal("Trocar óleo", e.Estado.ObservacoesDiagnostico);

        var inicio = e.IniciarExecucao(Servicos, T0.AddMinutes(3));
        Assert.True(inicio.Aceita);
        Assert.Equal(EtapaExecucao.AguardandoExecucao, e.Etapa);
        Assert.Equal(Servicos, e.Estado.Servicos);

        e.IniciarReparo("joao", T0.AddMinutes(4));
        Assert.Equal(EtapaExecucao.EmReparo, e.Etapa);

        var duracao = e.Finalizar("joao", T0.AddMinutes(10));
        Assert.Equal(360, duracao);
        Assert.Equal(EtapaExecucao.Finalizada, e.Etapa);
        Assert.True(e.Encerrada);
        Assert.Equal(T0.AddMinutes(10), e.Estado.EtapaDesde);

        Assert.Equal(
            [
                TipoApontamento.Abertura, TipoApontamento.DiagnosticoIniciado, TipoApontamento.DiagnosticoConcluido,
                TipoApontamento.ExecucaoIniciada, TipoApontamento.ReparoIniciado, TipoApontamento.Finalizada
            ],
            e.Apontamentos.Select(a => a.Tipo));
    }

    [Fact]
    public void Concluir_diagnostico_sem_observacoes_grava_nulo()
    {
        var e = EmEtapa(EtapaExecucao.EmDiagnostico);

        e.ConcluirDiagnostico("   ", "joao", T0.AddMinutes(2));

        Assert.Null(e.Estado.ObservacoesDiagnostico);
        Assert.Equal("Diagnóstico concluído.", e.Apontamentos[^1].Texto);
    }

    [Theory]
    [InlineData(EtapaExecucao.DiagnosticoPendente)]
    [InlineData(EtapaExecucao.DiagnosticoConcluido)]
    [InlineData(EtapaExecucao.Cancelada)]
    public void So_conclui_diagnostico_em_andamento(EtapaExecucao etapa)
    {
        var e = EmEtapa(etapa);

        var erro = Assert.Throws<EtapaInvalidaException>(() => e.ConcluirDiagnostico(null, "joao", T0.AddHours(1)));
        Assert.Contains(etapa.ToString(), erro.Message, StringComparison.Ordinal);
        Assert.Equal(etapa, e.Etapa);
    }

    [Theory]
    [InlineData(EtapaExecucao.EmDiagnostico)]
    [InlineData(EtapaExecucao.Cancelada)]
    public void So_inicia_diagnostico_pendente(EtapaExecucao etapa)
    {
        var e = EmEtapa(etapa);
        Assert.Throws<EtapaInvalidaException>(() => e.IniciarDiagnostico("maria", T0.AddHours(1)));
    }

    [Theory]
    [InlineData(EtapaExecucao.DiagnosticoPendente)]
    [InlineData(EtapaExecucao.EmDiagnostico)]
    public void Iniciar_execucao_antes_do_diagnostico_concluido_e_rejeitado_sem_mudar_a_etapa(EtapaExecucao etapa)
    {
        var e = EmEtapa(etapa);

        var resultado = e.IniciarExecucao(Servicos, T0.AddHours(1));

        Assert.False(resultado.Aceita);
        Assert.Contains("diagnóstico concluído", resultado.Motivo, StringComparison.Ordinal);
        Assert.Equal(etapa, e.Etapa);
        Assert.Equal(TipoApontamento.ExecucaoRejeitada, e.Apontamentos[^1].Tipo);
    }

    [Theory]
    [InlineData(EtapaExecucao.AguardandoExecucao)]
    [InlineData(EtapaExecucao.EmReparo)]
    [InlineData(EtapaExecucao.Finalizada)]
    [InlineData(EtapaExecucao.Cancelada)]
    public void Iniciar_execucao_depois_de_aceita_ou_encerrada_e_rejeitado_sem_mexer_na_execucao(EtapaExecucao etapa)
    {
        var e = EmEtapa(etapa);

        var resultado = e.IniciarExecucao(Servicos, T0.AddHours(1));

        Assert.False(resultado.Aceita);
        Assert.Equal(etapa, e.Etapa);
    }

    [Fact]
    public void Rejeitar_inicio_de_execucao_ainda_nao_iniciada_encerra_como_cancelada()
    {
        var e = EmEtapa(EtapaExecucao.DiagnosticoConcluido);

        e.RejeitarInicio("falha simulada", T0.AddHours(1));

        Assert.Equal(EtapaExecucao.Cancelada, e.Etapa);
        Assert.Equal("falha simulada", e.Estado.MotivoCancelamento);
        Assert.Equal(
            [TipoApontamento.ExecucaoRejeitada, TipoApontamento.Cancelada],
            e.Apontamentos.TakeLast(2).Select(a => a.Tipo));
    }

    [Theory]
    [InlineData(EtapaExecucao.DiagnosticoPendente)]
    [InlineData(EtapaExecucao.EmDiagnostico)]
    [InlineData(EtapaExecucao.DiagnosticoConcluido)]
    [InlineData(EtapaExecucao.AguardandoExecucao)]
    [InlineData(EtapaExecucao.EmReparo)]
    public void Cancelar_em_etapa_nao_final_encerra(EtapaExecucao etapa)
    {
        var e = EmEtapa(etapa);

        Assert.True(e.Cancelar("Orçamento recusado", T0.AddHours(1)));

        Assert.Equal(EtapaExecucao.Cancelada, e.Etapa);
        Assert.Equal("Orçamento recusado", e.Estado.MotivoCancelamento);
        Assert.Equal(T0.AddHours(1), e.Estado.CanceladaEm);
    }

    [Theory]
    [InlineData(EtapaExecucao.Finalizada)]
    [InlineData(EtapaExecucao.Cancelada)]
    public void Cancelar_em_etapa_final_e_ignorado(EtapaExecucao etapa)
    {
        var e = EmEtapa(etapa);
        var apontamentos = e.Apontamentos.Count;

        Assert.False(e.Cancelar("de novo", T0.AddHours(1)));

        Assert.Equal(etapa, e.Etapa);
        Assert.Equal(apontamentos, e.Apontamentos.Count);
    }

    [Fact]
    public void Cancelamento_antes_do_pedido_de_diagnostico_fica_registrado()
    {
        var e = Execucao.AbrirCancelada(42, Correlacao, "OS-20261116-0042", "Orçamento recusado", T0);

        Assert.Equal(EtapaExecucao.Cancelada, e.Etapa);
        Assert.Null(e.Estado.Veiculo);
        Assert.Equal(TipoApontamento.Cancelada, Assert.Single(e.Apontamentos).Tipo);
    }

    [Theory]
    [InlineData(EtapaExecucao.DiagnosticoPendente)]
    [InlineData(EtapaExecucao.DiagnosticoConcluido)]
    public void So_inicia_reparo_aguardando_execucao(EtapaExecucao etapa)
    {
        var e = EmEtapa(etapa);
        Assert.Throws<EtapaInvalidaException>(() => e.IniciarReparo("joao", T0.AddHours(1)));
    }

    [Theory]
    [InlineData(EtapaExecucao.AguardandoExecucao)]
    [InlineData(EtapaExecucao.Finalizada)]
    public void So_finaliza_reparo_em_andamento(EtapaExecucao etapa)
    {
        var e = EmEtapa(etapa);
        Assert.Throws<EtapaInvalidaException>(() => e.Finalizar("joao", T0.AddHours(1)));
    }

    [Fact]
    public void Observacao_entra_na_linha_do_tempo_com_o_autor()
    {
        var e = EmEtapa(EtapaExecucao.EmReparo);

        e.RegistrarObservacao("  Peça com desgaste  ", "maria", T0.AddMinutes(5));

        var obs = e.Apontamentos[^1];
        Assert.Equal(TipoApontamento.Observacao, obs.Tipo);
        Assert.Equal("Peça com desgaste", obs.Texto);
        Assert.Equal("maria", obs.Autor);
        Assert.Equal(EtapaExecucao.EmReparo, e.Etapa);
    }

    [Theory]
    [InlineData(EtapaExecucao.Finalizada)]
    [InlineData(EtapaExecucao.Cancelada)]
    public void Execucao_encerrada_nao_aceita_observacao(EtapaExecucao etapa)
    {
        var e = EmEtapa(etapa);
        Assert.Throws<EtapaInvalidaException>(() => e.RegistrarObservacao("tarde demais", "maria", T0.AddHours(1)));
    }

    [Fact]
    public void Marcar_como_gravada_avanca_a_versao_e_zera_os_novos_apontamentos()
    {
        var e = Nova();

        e.MarcarComoGravada();
        e.IniciarDiagnostico("joao", T0.AddMinutes(1));

        Assert.Equal(1, e.Version);
        Assert.Equal(TipoApontamento.DiagnosticoIniciado, Assert.Single(e.NovosApontamentos).Tipo);
        Assert.Equal(2, e.Apontamentos.Count);
    }

    [Fact]
    public void Restaurar_ordena_a_linha_do_tempo_e_nao_marca_nada_como_novo()
    {
        var original = EmEtapa(EtapaExecucao.EmDiagnostico);

        var restaurada = Execucao.Restaurar(original.Estado, original.Apontamentos.Reverse());

        Assert.Equal(original.Apontamentos, restaurada.Apontamentos);
        Assert.Empty(restaurada.NovosApontamentos);
        Assert.Equal(EtapaExecucao.EmDiagnostico, restaurada.Etapa);
    }

    [Fact]
    public void Abrir_valida_os_dados_obrigatorios()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Execucao.Abrir(0, Correlacao, "OS-1", Gol, null, T0));
        Assert.Throws<ArgumentException>(() => Execucao.Abrir(1, Correlacao, " ", Gol, null, T0));
        Assert.Throws<ArgumentNullException>(() => Execucao.Abrir(1, Correlacao, "OS-1", null!, null, T0));
    }
}
