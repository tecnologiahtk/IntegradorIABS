using IabsAgenteSenior.Configuracao;
using IabsAgenteSenior.Monitor;
using IabsAgenteSenior.Servico;

namespace IabsAgenteSenior.Testes;

public sealed class IconeBandejaTestes : IDisposable
{
    private static readonly DateTimeOffset Agora = new(2026, 11, 26, 10, 0, 0, TimeSpan.FromHours(-3));
    private readonly string _pasta = Path.Combine(Path.GetTempPath(), $"iabs-status-teste-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_pasta)) Directory.Delete(_pasta, recursive: true);
    }

    private static StatusAgente Status(bool conectado = true, bool configurado = true, int minutosAtras = 0, int foraHaMinutos = 0, string? mensagem = null) => new(
        "2.0.0", Agora.AddMinutes(-minutosAtras),
        configurado ? [Conexao("Produção", conectado, foraHaMinutos, mensagem)] : [],
        null);

    private static StatusConexao Conexao(string nome, bool conectado = true, int foraHaMinutos = 0, string? mensagem = null) => new(
        nome, conectado,
        conectado ? Agora.AddHours(-1) : null,
        conectado ? null : Agora.AddMinutes(-foraHaMinutos),
        Agora.AddMinutes(-3), 42, "Cliente HML", true, mensagem);

    private static StatusAgente Duas(StatusConexao producao, StatusConexao homologacao) => new("2.2.0", Agora, [producao, homologacao], null);

    [Fact]
    public void Verde_quando_conectado()
    {
        var a = CalculoAparencia.Calcular(Status(), "Running", Agora);
        Assert.Equal(Nivel.Ok, a.Nivel);
        Assert.Contains(a.Linhas, l => l.StartsWith("Conectado desde 26/11", StringComparison.Ordinal));
        Assert.Contains("Lançar atestados: liberado", a.Linhas);
        Assert.True(a.Dica.Length <= 127);
    }

    [Fact]
    public void Vermelho_quando_servico_parado_ou_nao_instalado()
    {
        Assert.Equal(Nivel.Erro, CalculoAparencia.Calcular(Status(), "Stopped", Agora).Nivel);
        Assert.Equal(Nivel.Erro, CalculoAparencia.Calcular(null, null, Agora).Nivel);
    }

    [Fact]
    public void Queda_curta_e_amarela_e_longa_e_vermelha_com_a_mensagem()
    {
        Assert.Equal(Nivel.Atencao, CalculoAparencia.Calcular(Status(conectado: false, foraHaMinutos: 1), "Running", Agora).Nivel);
        var a = CalculoAparencia.Calcular(Status(conectado: false, foraHaMinutos: 10, mensagem: "A IABS recusou a chave do integrador."), "Running", Agora);
        Assert.Equal(Nivel.Erro, a.Nivel);
        Assert.Equal("A IABS recusou a chave do integrador.", a.Linhas[0]);
    }

    [Theory]
    [InlineData(false, 0)]   // aguardando configuração
    [InlineData(true, 30)]   // serviço sem dar notícias
    public void Amarelo(bool configurado, int minutosAtras) =>
        Assert.Equal(Nivel.Atencao, CalculoAparencia.Calcular(Status(configurado: configurado, minutosAtras: minutosAtras), "Running", Agora).Nivel);

    [Fact]
    public void Status_gravado_e_lido_de_volta()
    {
        var caminhos = Caminhos.Resolver(_pasta);
        var status = Duas(Conexao("Produção"), Conexao("Homologação", conectado: false, foraHaMinutos: 5, mensagem: "Conectando à IABS..."));
        StatusPainel.Gravar(caminhos, status);
        var lido = StatusPainel.Ler(caminhos)!;
        Assert.Equal(status.Versao, lido.Versao);
        Assert.Equal(status.AtualizadoEm, lido.AtualizadoEm);
        Assert.Equal(status.Lista, lido.Lista);
    }

    [Fact]
    public void Duas_conexoes_ok_e_verde_com_uma_linha_por_conexao()
    {
        var a = CalculoAparencia.Calcular(Duas(Conexao("Produção"), Conexao("Homologação")), "Running", Agora);
        Assert.Equal(Nivel.Ok, a.Nivel);
        Assert.Equal("conectado à IABS (2 conexões)", a.Titulo);
        Assert.Equal(2, a.Linhas.Count);
        Assert.StartsWith("Produção: conectado desde 26/11", a.Linhas[0], StringComparison.Ordinal);
        Assert.StartsWith("Homologação: conectado desde 26/11", a.Linhas[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Homologacao_fora_ha_muito_tempo_deixa_vermelho_e_diz_qual()
    {
        var a = CalculoAparencia.Calcular(
            Duas(Conexao("Produção"), Conexao("Homologação", conectado: false, foraHaMinutos: 12, mensagem: "A IABS recusou a chave do integrador.")),
            "Running", Agora);
        Assert.Equal(Nivel.Erro, a.Nivel);
        Assert.Equal("sem conexão com a IABS (Homologação)", a.Titulo);
        Assert.Equal("Homologação: sem conexão há 12 min. A IABS recusou a chave do integrador.", a.Linhas[1]);
        Assert.True(a.Dica.Length <= 127);
    }

    [Fact]
    public void Queda_curta_numa_das_conexoes_e_amarela() =>
        Assert.Equal("conectando à IABS (Homologação)",
            CalculoAparencia.Calcular(Duas(Conexao("Produção"), Conexao("Homologação", conectado: false, foraHaMinutos: 1)), "Running", Agora).Titulo);

    [Fact]
    public void Status_da_2_1_sem_a_lista_aguarda_o_servico_novo()
    {
        var caminhos = Caminhos.Resolver(_pasta);
        Directory.CreateDirectory(_pasta);
        File.WriteAllText(StatusPainel.Arquivo(caminhos),
            """{"versao":"2.1.4","atualizadoEm":"2026-11-26T10:00:00-03:00","configurado":true,"conectado":true,"pedidos":3,"gravacaoLiberada":true}""");
        var lido = StatusPainel.Ler(caminhos)!;
        Assert.False(lido.Configurado);
        Assert.Equal(Nivel.Atencao, CalculoAparencia.Calcular(lido, "Running", Agora).Nivel);
    }

    [Fact]
    public void Sem_arquivo_de_status_le_nulo() =>
        Assert.Null(StatusPainel.Ler(Caminhos.Resolver(_pasta)));
}
